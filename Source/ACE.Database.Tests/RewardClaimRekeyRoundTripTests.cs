using System;
using System.Linq;
using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// ShardDatabase.TryRekeyRewardClaims against a real shard database: the reward-claim carry-forward that runs
    /// when an account's IP household changes. The decision delegates here mirror the server's
    /// RewardClaimCarryForward.TargetIpKey (ACE.Database.Tests cannot reference ACE.Server); what is under test is
    /// the DAO: the NULL-key filter, the UNIQUE (claim_Key, ip_Key) collision probe and its NULL fallback, and the
    /// transaction opened inside the execution strategy.
    ///
    /// THIS WRITES ROWS. It runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/> is 1, a promise that
    /// Config.js's Shard block points at a DISPOSABLE database with
    /// Database/Updates/Shard/2026-09-15-01-Add-Reward-Claim.sql applied. Each test uses a claim key unique to the
    /// run and deletes every row under it afterwards.
    /// </summary>
    [TestClass]
    public class RewardClaimRekeyRoundTripTests
    {
        private const string X = "203.0.113.50";

        private static ShardDatabase RequireDisposableShard()
        {
            if (Environment.GetEnvironmentVariable(PvpArenaRoundTripTests.OptInVariable) != "1")
                Assert.Inconclusive($"Skipped: set {PvpArenaRoundTripTests.OptInVariable}=1 only when Config.js points at a disposable shard database. This test writes rows.");

            TestEnvironment.InitializeConfig();

            var shardDb = new ShardDatabase();

            bool reachable;

            try
            {
                reachable = shardDb.Exists(false);
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive("Skipped: the shard database is not reachable with the configured Config.js.");

            return shardDb;
        }

        private static string NewKey() => "rekey-test-" + Guid.NewGuid().ToString("N").Substring(0, 16);

        private static uint NewAccountBase() => 0xF0000000u + (uint)new Random().Next(0, 0x0FFFFF00);

        private static RewardClaimInsertResult Claim(ShardDatabase db, string key, uint account, string ipKey, string ipAddress)
            => db.TryInsertRewardClaim(new RewardClaim
            {
                ClaimKey = key,
                AccountId = account,
                IpKey = ipKey,
                IpAddress = ipAddress,
                CharacterId = 0x50000001,
                NpcWcid = 1,
                ClaimToken = Guid.NewGuid().ToString("N"),
            });

        private static RewardClaim Row(string key, uint account)
        {
            using var context = new ShardDbContext();
            return context.RewardClaim.AsNoTracking().Single(r => r.ClaimKey == key && r.AccountId == account);
        }

        private static void DeleteKeys(params string[] keys)
        {
            using var context = new ShardDbContext();
            context.RewardClaim.Where(r => keys.Contains(r.ClaimKey)).ExecuteDelete();
        }

        // Mirrors RewardClaimCarryForward.TargetIpKey.
        private static Func<RewardClaim, bool, string> EnterHousehold(string householdKey)
            => (row, held) => held ? null : householdKey;

        private static Func<RewardClaim, bool, string> LeaveToAddress()
            => (row, held) =>
            {
                if (held || string.IsNullOrWhiteSpace(row.IpAddress) || !IPAddress.TryParse(row.IpAddress, out var address))
                    return null;

                return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
            };

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Joining_a_cluster_moves_the_row_and_frees_the_old_address()
        {
            var db = RequireDisposableShard();
            var key = NewKey();
            var b = NewAccountBase();
            var d = b + 1;
            var clusterKey = "cluster:t" + key.Substring(11, 8);

            try
            {
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key, b, X, X));
                Assert.AreEqual(RewardClaimInsertResult.Duplicate, Claim(db, key, d, X, X), "control: B holds X before the move");

                Assert.IsTrue(db.TryRekeyRewardClaims(b, EnterHousehold(clusterKey), out var result));

                Assert.AreEqual(1, result.Rekeyed);
                Assert.AreEqual(clusterKey, Row(key, b).IpKey);
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key, d, X, X), "a third account under X may now claim");
                Assert.AreEqual(RewardClaimInsertResult.Duplicate, Claim(db, key, b, null, X), "B still cannot claim twice");
            }
            finally
            {
                DeleteKeys(key);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void A_collision_nulls_the_row_and_the_primary_key_row_remains()
        {
            var db = RequireDisposableShard();
            var key = NewKey();
            var b = NewAccountBase();
            var e = b + 2;
            var clusterKey = "cluster:t" + key.Substring(11, 8);

            try
            {
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key, e, clusterKey, "198.51.100.5"));
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key, b, X, X));

                Assert.IsTrue(db.TryRekeyRewardClaims(b, EnterHousehold(clusterKey), out var result));

                Assert.AreEqual(1, result.NulledByCollision);
                Assert.AreEqual(0, result.Rekeyed);
                Assert.IsNull(Row(key, b).IpKey, "B's row is nulled, not skipped");
                Assert.AreEqual(clusterKey, Row(key, e).IpKey, "the holder is untouched");
                Assert.AreEqual(RewardClaimInsertResult.Duplicate, Claim(db, key, b, null, X), "the PRIMARY KEY row remains");
            }
            finally
            {
                DeleteKeys(key);
            }
        }

        /// <summary>
        /// A collision in the MIDDLE of an account's rows: the 1062 on row 2 must roll back only that statement,
        /// so row 1 (already written) and row 3 (written after the fallback) both commit. Rows are processed in
        /// claim_Key order, and the keys share a prefix with suffixes -1/-2/-3, so row 2 is the second processed.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void A_collision_on_the_second_of_three_rows_nulls_only_that_row()
        {
            var db = RequireDisposableShard();
            var prefix = NewKey();
            var key1 = prefix + "-1";
            var key2 = prefix + "-2";
            var key3 = prefix + "-3";
            var b = NewAccountBase();
            var e = b + 2;
            var clusterKey = "cluster:t" + prefix.Substring(11, 8);

            try
            {
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key2, e, clusterKey, "198.51.100.5"), "another account already holds key2 under the cluster");
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key1, b, X, X));
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key2, b, X, X));
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key3, b, X, X));

                Assert.IsTrue(db.TryRekeyRewardClaims(b, EnterHousehold(clusterKey), out var result));

                Assert.AreEqual(3, result.Examined);
                Assert.AreEqual(2, result.Rekeyed);
                Assert.AreEqual(1, result.NulledByCollision);
                Assert.AreEqual(clusterKey, Row(key1, b).IpKey, "row 1 re-keyed");
                Assert.IsNull(Row(key2, b).IpKey, "row 2 collided and went NULL");
                Assert.AreEqual(clusterKey, Row(key3, b).IpKey, "row 3 re-keyed after the collision");
                Assert.AreEqual(clusterKey, Row(key2, e).IpKey, "the holder is untouched");
            }
            finally
            {
                DeleteKeys(key1, key2, key3);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Leaving_reverts_each_row_to_its_own_address_and_skips_null_keys()
        {
            var db = RequireDisposableShard();
            var key1 = NewKey();
            var key2 = NewKey();
            var key3 = NewKey();
            var b = NewAccountBase();
            var clusterKey = "cluster:t" + key1.Substring(11, 8);

            try
            {
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key1, b, clusterKey, "::ffff:" + X));
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key2, b, clusterKey, null));
                Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(db, key3, b, null, "198.51.100.7"));

                Assert.IsTrue(db.TryRekeyRewardClaims(b, LeaveToAddress(), out var result));

                Assert.AreEqual(2, result.Examined, "the NULL-key bypass row is not even read");
                Assert.AreEqual(1, result.Rekeyed);
                Assert.AreEqual(1, result.NulledNoKey);
                Assert.AreEqual(X, Row(key1, b).IpKey);
                Assert.IsNull(Row(key2, b).IpKey);
                Assert.IsNull(Row(key3, b).IpKey, "a bypass row stays NULL");
            }
            finally
            {
                DeleteKeys(key1, key2, key3);
            }
        }
    }
}
