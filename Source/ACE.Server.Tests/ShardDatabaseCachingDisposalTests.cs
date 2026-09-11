using System;
using System.Threading;

using ACE.Database;
using ACE.Database.Models.Shard;

using Microsoft.EntityFrameworkCore;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using RuntimeBiota = ACE.Entity.Models.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SaveBiota's cache-MISS path owns the ShardDbContext it creates, and the biota cache is the only thing
    /// that would otherwise keep that context alive. Every exit where the cache does NOT take it must dispose
    /// it, or the call leaks one pooled MySQL connection.
    ///
    /// This became load-bearing when DiscardCachedBiota started dropping a cache entry whose save failed:
    /// before that, a failing warm biota kept reusing one retained context, and now every retry arrives on the
    /// miss path instead. A player is bounded by BiotaSaveFailed disconnecting them, but a creature, corpse or
    /// container has no such circuit breaker and would leak one connection per autosave heartbeat.
    ///
    /// The test drives the real SaveBiota against a context pointed at a closed port, so staging throws. That
    /// covers the THROW exit only - the false-return, doNotAddToCache and zero-retention exits all require
    /// DoSaveBiota to actually run against a reachable database, which this suite deliberately does not have
    /// (see ACE.Database.Tests for the ones that do). What it does prove is that the finally fires and that the
    /// disposal decision is wired to the cache-ownership flag rather than to the return value.
    /// </summary>
    [TestClass]
    public class ShardDatabaseCachingDisposalTests
    {
        private sealed class TrackingShardDbContext : ShardDbContext
        {
            public bool Disposed;

            public TrackingShardDbContext(DbContextOptions<ShardDbContext> options) : base(options) { }

            public override void Dispose()
            {
                Disposed = true;

                base.Dispose();
            }
        }

        /// <summary>
        /// Real ShardDatabaseWithCaching, real SaveBiota, but every miss-path context points at a closed port
        /// so the first query throws instead of hanging. Retention is deliberately non-zero, so the cache WOULD
        /// have taken the context had the save succeeded - the flag has to be the reason it is disposed, not a
        /// zero retention time.
        /// </summary>
        private sealed class UnreachableShardDatabase : ShardDatabaseWithCaching
        {
            public TrackingShardDbContext LastContext;

            public UnreachableShardDatabase() : base(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)) { }

            protected override ShardDbContext CreateShardDbContext()
            {
                var options = new DbContextOptionsBuilder<ShardDbContext>()
                    .UseMySql("server=127.0.0.1;port=1;user=none;password=none;database=none;Connection Timeout=1;Default Command Timeout=1",
                        new MySqlServerVersion(new Version(8, 0, 36)))
                    .Options;

                LastContext = new TrackingShardDbContext(options);

                return LastContext;
            }
        }

        [TestMethod]
        public void SaveBiota_WhenTheMissPathThrows_DisposesItsContext()
        {
            var database = new UnreachableShardDatabase();
            var biota = new RuntimeBiota { Id = 0x50000010, WeenieClassId = 1 };
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

            var threw = false;

            try
            {
                database.SaveBiota(biota, rwLock);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "the unreachable database was expected to fail the staging query");
            Assert.IsNotNull(database.LastContext, "SaveBiota should have taken the cache-miss path");
            Assert.IsTrue(database.LastContext.Disposed, "a context the cache did not take must be disposed on the way out");
            Assert.AreEqual(0, database.GetBiotaCacheKeys().Count, "nothing should have been cached");
        }
    }
}
