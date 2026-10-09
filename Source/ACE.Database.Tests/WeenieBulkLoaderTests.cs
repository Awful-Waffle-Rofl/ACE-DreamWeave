using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Adapter;
using ACE.Database.Extensions;
using ACE.Database.Models.World;
using ACE.Entity.Enum;

namespace ACE.Database.Tests
{
    /// <summary>
    /// The bulk weenie loader (WorldDatabase.GetWeeniesCore, WorldDatabaseWithEntityCache.PrefetchWeenies). All of
    /// these read a real world database and write nothing to it. BulkMatchesPerId_AllWeenies is the fidelity gate:
    /// exhaustive, never sampled, and it FAILS rather than skipping when CI sets ACE_REQUIRE_WORLD_DB=1.
    /// </summary>
    [TestClass]
    public class WeenieBulkLoaderTests
    {
        // A plain WorldDatabase, never the caching subclass: the reference reads must not be able to write a cache.
        private static readonly WorldDatabase referenceDb = new WorldDatabase();

        [TestInitialize]
        public void ResetSettings()
        {
            WeenieBulkLoadSettings.EnabledProvider = null;
            WeenieBulkLoadSettings.SelfCheckSampleProvider = null;
        }

        [TestCleanup]
        public void RestoreSettings()
        {
            WeenieBulkLoadSettings.EnabledProvider = null;
            WeenieBulkLoadSettings.SelfCheckSampleProvider = null;
        }

        private static WorldDbContext NewNoTrackingContext()
        {
            var context = new WorldDbContext();
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return context;
        }

        private static List<uint> AllWeenieIds()
        {
            using (var context = NewNoTrackingContext())
                return context.Weenie.Select(r => r.ClassId).OrderBy(id => id).ToList();
        }

        /// <summary>
        /// Every weenie in the database, three ways: the legacy per-id reader (the reference), the core with a
        /// single-element list (what the per-id miss path now runs), and the core over 250-id chunks (what a bulk
        /// prefetch runs). All three are converted to the runtime Weenie and compared member by member, order
        /// included. Chunks run on 4 threads, each with its own context, to keep the CI cost down.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        [TestCategory("WeenieFidelity")]
        public void BulkMatchesPerId_AllWeenies()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var stopwatch = Stopwatch.StartNew();
            var ids = AllWeenieIds();

            Assert.IsTrue(ids.Count > 0, "the world database has no weenies, so nothing would be compared");

            var compared = 0;
            var failures = new ConcurrentQueue<string>();
            var failureCount = 0;

            Parallel.ForEach(ids.Chunk(WorldDatabase.DefaultWeenieChunkSize), new ParallelOptions { MaxDegreeOfParallelism = 4 }, chunk =>
            {
                using (var context = NewNoTrackingContext())
                {
                    var bulk = referenceDb.GetWeeniesCore(context, chunk);

                    foreach (var id in chunk)
                    {
                        var legacyEntity = referenceDb.GetWeenieLegacy(context, id);
                        var perIdEntity = referenceDb.GetWeeniesCore(context, new[] { id }).GetValueOrDefault(id);
                        var bulkEntity = bulk.GetValueOrDefault(id);

                        var legacy = legacyEntity == null ? null : WeenieConverter.ConvertToEntityWeenie(legacyEntity);
                        var perId = perIdEntity == null ? null : WeenieConverter.ConvertToEntityWeenie(perIdEntity);
                        var bulkConverted = bulkEntity == null ? null : WeenieConverter.ConvertToEntityWeenie(bulkEntity);

                        var perIdDiff = WeenieFidelity.FirstDifference(perId, legacy);
                        var bulkDiff = WeenieFidelity.FirstDifference(bulkConverted, legacy);

                        if (legacy == null)
                            perIdDiff ??= "legacy reader returned null for an id listed in the weenie table";

                        if (perIdDiff != null || bulkDiff != null)
                        {
                            if (Interlocked.Increment(ref failureCount) <= 25)
                                failures.Enqueue($"wcid {id}: " + (perIdDiff != null ? $"core per-id vs legacy at {perIdDiff}" : "") + (bulkDiff != null ? $" core bulk vs legacy at {bulkDiff}" : ""));
                        }

                        Interlocked.Increment(ref compared);
                    }
                }
            });

            Console.WriteLine($"BulkMatchesPerId_AllWeenies: compared {compared} weenies (legacy per-id vs core per-id vs core bulk) in {stopwatch.Elapsed.TotalSeconds:N1}s, {failureCount} differing.");

            Assert.AreEqual(ids.Count, compared, "not every weenie was compared");
            Assert.IsTrue(compared > 0, "compared zero weenies");
            Assert.AreEqual(0, failureCount, $"{failureCount} weenie(s) differ from the legacy per-id read. First {failures.Count}:\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// A mixed set - creatures, books, non-creatures carrying creature-table rows, missing ids - loaded with
        /// chunk size 7 and 5000 must convert identically.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void ChunkSizeInvariant()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = MixedIdSet(out _);

            using (var context = NewNoTrackingContext())
            {
                var small = referenceDb.GetWeeniesCore(context, ids, 7);
                var large = referenceDb.GetWeeniesCore(context, ids, 5000);

                Assert.IsTrue(small.Count > 0, "loaded nothing");
                CollectionAssert.AreEquivalent(small.Keys.ToList(), large.Keys.ToList());

                foreach (var id in small.Keys)
                {
                    var diff = WeenieFidelity.FirstDifference(WeenieConverter.ConvertToEntityWeenie(small[id]), WeenieConverter.ConvertToEntityWeenie(large[id]));
                    Assert.IsNull(diff, $"wcid {id}: chunk 7 vs chunk 5000 at {diff}");
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void MissingIdsNegativelyCached()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var missing = MissingIds(3);
            var cache = new WorldDatabaseWithEntityCache();

            var missesBefore = WorldDatabaseWithEntityCache.WeenieCacheMissTotal;

            var result = cache.PrefetchWeenies(missing, Timeout.InfiniteTimeSpan);

            Assert.AreEqual(missing.Count, result.NegativelyCached, result.ToString());
            Assert.AreEqual(0, result.Loaded, result.ToString());

            foreach (var id in missing)
            {
                Assert.IsTrue(cache.IsWeenieCached(id), $"wcid {id} was not cached as a miss");
                Assert.IsNull(cache.PeekCachedWeenie(id));
                Assert.IsNull(cache.GetCachedWeenie(id), "a cached miss must read back as null");
            }

            Assert.AreEqual(missesBefore, WorldDatabaseWithEntityCache.WeenieCacheMissTotal, "a prefetch, or a read of a cached miss, counted as a cache miss");
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void DoesNotReplaceCachedInstance()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(40).ToList();
            var cache = new WorldDatabaseWithEntityCache();

            var existing = cache.GetCachedWeenie(ids[0]);
            Assert.IsNotNull(existing);

            var result = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);

            Assert.AreEqual(1, result.AlreadyCached, result.ToString());
            Assert.AreEqual(ids.Count - 1, result.Loaded, result.ToString());
            Assert.AreSame(existing, cache.PeekCachedWeenie(ids[0]), "the prefetch replaced an already cached instance");

            // A second prefetch of the same ids finds everything cached and reads nothing.
            var again = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);
            Assert.AreEqual(ids.Count, again.AlreadyCached, again.ToString());
            Assert.AreEqual(0, again.ChunksAttempted, again.ToString());
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void DoesNotReplaceInstanceCachedDuringTheRead()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(20).ToList();
            var cache = new WorldDatabaseWithEntityCache();

            ACE.Entity.Models.Weenie racer = null;
            cache.BulkChunkBeforePublishHook = () => racer ??= cache.GetCachedWeenie(ids[5]);

            var result = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);

            Assert.IsNotNull(racer);
            Assert.AreEqual(1, result.LostRace, result.ToString());
            Assert.AreSame(racer, cache.PeekCachedWeenie(ids[5]), "the publish replaced an instance cached while the chunk was being read");
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void DropsChunkWhenClearedMidFill()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(30).ToList();

            // Full clear.
            var cache = new WorldDatabaseWithEntityCache();
            var cleared = false;
            cache.BulkChunkBeforePublishHook = () =>
            {
                if (!cleared)
                {
                    cleared = true;
                    cache.ClearWeenieCache();
                }
            };

            var result = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);

            Assert.AreEqual(1, result.ChunksStale, result.ToString());
            Assert.AreEqual(0, result.Loaded, result.ToString());
            Assert.IsFalse(ids.Any(cache.IsWeenieCached), "a chunk read before a clear was published after it");

            // Single-wcid clear (/import), which must drop the chunk the same way.
            var cache2 = new WorldDatabaseWithEntityCache();
            var cleared2 = false;
            cache2.BulkChunkBeforePublishHook = () =>
            {
                if (!cleared2)
                {
                    cleared2 = true;
                    cache2.ClearCachedWeenie(ids[3]);
                }
            };

            var result2 = cache2.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);

            Assert.AreEqual(1, result2.ChunksStale, result2.ToString());
            Assert.IsFalse(ids.Any(cache2.IsWeenieCached));

            // Without a clear the same ids publish.
            var cache3 = new WorldDatabaseWithEntityCache();
            var result3 = cache3.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);
            Assert.AreEqual(ids.Count, result3.Loaded, result3.ToString());
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void RejectsChunkOnSelfCheckMismatch()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(60).ToList();
            var target = ids[17];
            var cache = new WorldDatabaseWithEntityCache();

            var rejectedBefore = WorldDatabaseWithEntityCache.BulkChunksRejectedTotal;

            // Corrupt one freshly read weenie the way a fidelity bug would: an extra int row the per-id read lacks.
            cache.BulkChunkReadHook = entities =>
            {
                if (entities.TryGetValue(target, out var weenie))
                    weenie.WeeniePropertiesInt.Add(new WeeniePropertiesInt { ObjectId = target, Type = 65000, Value = 12345 });
            };

            var result = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan, chunkSize: 30, selfCheckSample: 1000);

            Assert.AreEqual(2, result.ChunksAttempted, result.ToString());
            Assert.AreEqual(1, result.ChunksRejected, result.ToString());
            Assert.AreEqual(1, result.ChunksPublished, result.ToString());
            Assert.AreEqual(1, result.SelfCheckMismatches, result.ToString());
            StringAssert.Contains(result.FirstMismatch, $"wcid {target}");
            StringAssert.Contains(result.FirstMismatch, "PropertiesInt");
            Assert.AreEqual(rejectedBefore + 1, WorldDatabaseWithEntityCache.BulkChunksRejectedTotal);

            // Nothing from the rejected chunk (ids 0-29, which holds the target) was published; the other chunk was.
            Assert.IsFalse(ids.Take(30).Any(cache.IsWeenieCached), "a rejected chunk published something");
            Assert.IsTrue(ids.Skip(30).All(cache.IsWeenieCached), "the clean chunk was not published");

            // The lazy path still serves the rejected ids correctly.
            Assert.IsNull(WeenieFidelity.FirstDifference(cache.GetCachedWeenie(target), cache.ReadWeenieUncached(target)));
        }

        /// <summary>
        /// A throw part-way through one chunk's publish is contained to that chunk: PrefetchWeenies returns, the other
        /// chunk still publishes, what was added before the throw stays (and is exactly right), and GetCachedWeenies
        /// still returns every wcid.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PublishFailureIsContainedToItsChunk()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(60).ToList();
            var poison = ids[10]; // the 11th wcid of the first 30-wcid chunk
            var cache = new WorldDatabaseWithEntityCache();

            var publishFailedBefore = WorldDatabaseWithEntityCache.BulkChunksPublishFailedTotal;

            cache.BulkChunkPublishHook = id =>
            {
                if (id == poison)
                    throw new InvalidOperationException("injected publish failure");
            };

            var result = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan, chunkSize: 30, selfCheckSample: 0);

            Assert.AreEqual(2, result.ChunksAttempted, result.ToString());
            Assert.AreEqual(1, result.ChunksPublishFailed, result.ToString());
            Assert.AreEqual(1, result.ChunksPublished, result.ToString());
            Assert.AreEqual(publishFailedBefore + 1, WorldDatabaseWithEntityCache.BulkChunksPublishFailedTotal);

            // HashSet-driven, so the first chunk's order is not the list order: find what got in before the throw.
            var firstChunk = ids.Take(30).ToList();
            var addedBeforeThrow = firstChunk.Count(cache.IsWeenieCached);

            Assert.IsFalse(cache.IsWeenieCached(poison), "the wcid whose publish threw was cached");
            Assert.IsTrue(addedBeforeThrow < firstChunk.Count, "the failed chunk published completely");
            Assert.AreEqual(30 + addedBeforeThrow, result.Loaded, "Loaded must count the adds made before the throw: " + result);
            Assert.IsTrue(ids.Skip(30).All(cache.IsWeenieCached), "the other chunk did not publish");

            // What the failed chunk did add is correct.
            foreach (var id in firstChunk.Where(cache.IsWeenieCached))
                Assert.IsNull(WeenieFidelity.FirstDifference(cache.PeekCachedWeenie(id), cache.ReadWeenieUncached(id)), $"wcid {id}");

            // Never partial: the lazy path fills the rest, even with the hook still throwing on every publish.
            var complete = cache.GetCachedWeenies(ids, Timeout.InfiniteTimeSpan);

            Assert.AreEqual(ids.Count, complete.Count);
            Assert.IsTrue(complete.Values.All(w => w != null), "GetCachedWeenies returned a missing weenie");
            Assert.IsNull(WeenieFidelity.FirstDifference(complete[poison], cache.ReadWeenieUncached(poison)));
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void KillSwitchMakesPrefetchANoOp()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(10).ToList();
            var cache = new WorldDatabaseWithEntityCache();

            WeenieBulkLoadSettings.EnabledProvider = () => false;

            var result = cache.PrefetchWeenies(ids, Timeout.InfiniteTimeSpan);

            Assert.IsTrue(result.Disabled);
            Assert.IsFalse(ids.Any(cache.IsWeenieCached), "a disabled prefetch cached something");

            // The per-id path with the switch off is the legacy reader, and still answers correctly.
            var weenies = cache.GetCachedWeenies(ids, Timeout.InfiniteTimeSpan);
            Assert.AreEqual(ids.Count, weenies.Count);
            Assert.IsTrue(weenies.Values.All(w => w != null));
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GateTimeoutSkipsAndCounts()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            var ids = AllWeenieIds().Take(10).ToList();
            var cache = new WorldDatabaseWithEntityCache();

            var timeoutsBefore = WorldDatabaseWithEntityCache.BulkGateTimeoutTotal;

            for (var i = 0; i < WorldDatabaseWithEntityCache.BulkGateConcurrency; i++)
                Assert.IsTrue(cache.BulkGate.Wait(0));

            try
            {
                var result = cache.PrefetchWeenies(ids, TimeSpan.Zero);

                Assert.AreEqual(1, result.GateTimeouts, result.ToString());
                Assert.AreEqual(ids.Count, result.IdsSkipped, result.ToString());
                Assert.AreEqual(0, result.ChunksAttempted, result.ToString());
                Assert.IsFalse(ids.Any(cache.IsWeenieCached));
                Assert.AreEqual(timeoutsBefore + 1, WorldDatabaseWithEntityCache.BulkGateTimeoutTotal);

                // GetCachedWeenies never returns partial results: the per-id path covers what the gate skipped.
                var complete = cache.GetCachedWeenies(ids, TimeSpan.Zero);
                Assert.AreEqual(ids.Count, complete.Count);
                Assert.IsTrue(complete.Values.All(w => w != null));
            }
            finally
            {
                cache.BulkGate.Release(WorldDatabaseWithEntityCache.BulkGateConcurrency);
            }
        }

        /// <summary>
        /// Structural guards: the core assigns every navigation the scaffolded model has, and FirstDifference
        /// compares every member the runtime Weenie has. Then, behaviourally, for every table: an id that has rows
        /// there (and passes that table's creature/book gate) loads a non-empty collection, of the same size the
        /// legacy reader loads.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void EveryWorldCollectionAssigned()
        {
            var navigations = typeof(Weenie).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType != typeof(string) && (typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType.Namespace == typeof(Weenie).Namespace))
                .Select(p => p.Name)
                .ToList();

            CollectionAssert.AreEquivalent(navigations, WorldDatabase.CoreAssignedNavigations.ToList(), "GetWeeniesCore does not assign exactly the scaffolded Weenie navigations");

            var entityMembers = typeof(ACE.Entity.Models.Weenie).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
            CollectionAssert.AreEquivalent(entityMembers, WeenieFidelity.ComparedMembers.ToList(), "WeenieFidelity does not compare exactly the runtime Weenie's members");

            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            using (var context = NewNoTrackingContext())
            {
                var types = context.Weenie.Select(w => new { w.ClassId, w.Type }).ToDictionary(w => w.ClassId, w => (WeenieType)w.Type);

                uint Pick(IQueryable<uint> objectIds, Func<WeenieType, bool> gate)
                {
                    foreach (var id in objectIds.Distinct().OrderBy(id => id).Take(2000).ToList())
                        if (types.TryGetValue(id, out var type) && gate(type))
                            return id;

                    return 0;
                }

                bool Any(WeenieType _) => true;

                var probes = new (string Navigation, uint Id)[]
                {
                    (nameof(Weenie.WeeniePropertiesBool), Pick(context.WeeniePropertiesBool.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesDID), Pick(context.WeeniePropertiesDID.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesFloat), Pick(context.WeeniePropertiesFloat.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesIID), Pick(context.WeeniePropertiesIID.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesInt), Pick(context.WeeniePropertiesInt.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesInt64), Pick(context.WeeniePropertiesInt64.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesPosition), Pick(context.WeeniePropertiesPosition.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesString), Pick(context.WeeniePropertiesString.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesAnimPart), Pick(context.WeeniePropertiesAnimPart.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesAttribute), Pick(context.WeeniePropertiesAttribute.Select(r => r.ObjectId), WorldDatabase.IsCreatureWeenieType)),
                    (nameof(Weenie.WeeniePropertiesAttribute2nd), Pick(context.WeeniePropertiesAttribute2nd.Select(r => r.ObjectId), WorldDatabase.IsCreatureWeenieType)),
                    (nameof(Weenie.WeeniePropertiesBodyPart), Pick(context.WeeniePropertiesBodyPart.Select(r => r.ObjectId), WorldDatabase.IsCreatureWeenieType)),
                    (nameof(Weenie.WeeniePropertiesBook), Pick(context.WeeniePropertiesBook.Select(r => r.ObjectId), WorldDatabase.IsBookWeenieType)),
                    (nameof(Weenie.WeeniePropertiesBookPageData), Pick(context.WeeniePropertiesBookPageData.Select(r => r.ObjectId), WorldDatabase.IsBookWeenieType)),
                    (nameof(Weenie.WeeniePropertiesCreateList), Pick(context.WeeniePropertiesCreateList.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesEmote), Pick(context.WeeniePropertiesEmote.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesEventFilter), Pick(context.WeeniePropertiesEventFilter.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesGenerator), Pick(context.WeeniePropertiesGenerator.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesPalette), Pick(context.WeeniePropertiesPalette.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesSkill), Pick(context.WeeniePropertiesSkill.Select(r => r.ObjectId), WorldDatabase.IsCreatureWeenieType)),
                    (nameof(Weenie.WeeniePropertiesSpellBook), Pick(context.WeeniePropertiesSpellBook.Select(r => r.ObjectId), Any)),
                    (nameof(Weenie.WeeniePropertiesTextureMap), Pick(context.WeeniePropertiesTextureMap.Select(r => r.ObjectId), Any)),
                };

                CollectionAssert.AreEquivalent(WorldDatabase.CoreAssignedNavigations.ToList(), probes.Select(p => p.Navigation).ToList(), "a navigation has no behavioural probe");

                var loaded = referenceDb.GetWeeniesCore(context, probes.Where(p => p.Id != 0).Select(p => p.Id).ToList());

                foreach (var (navigation, id) in probes)
                {
                    Assert.AreNotEqual(0u, id, $"no weenie in this database has {navigation} rows and passes its gate, so the probe proves nothing");

                    var property = typeof(Weenie).GetProperty(navigation);
                    var core = property.GetValue(loaded[id]);
                    var legacy = property.GetValue(referenceDb.GetWeenieLegacy(context, id));

                    Assert.AreEqual(Size(legacy), Size(core), $"{navigation} for wcid {id}: legacy vs core size");
                    Assert.IsTrue(Size(core) > 0, $"{navigation} for wcid {id} loaded empty although it has rows");
                }
            }
        }

        private static int Size(object navigationValue)
        {
            if (navigationValue == null)
                return 0;

            if (navigationValue is System.Collections.ICollection collection)
                return collection.Count;

            return 1; // a singular navigation (WeeniePropertiesBook) that is set
        }

        /// <summary>
        /// Pins the SQL shape EF/Pomelo generates for the chunk queries (revision 14): the id list must become a
        /// literal IN list, not a JSON_TABLE parameter join, and the explicit ORDER BY must survive.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void ChunkQuerySqlShapeIsPinned()
        {
            TestEnvironment.RequireWorldDatabaseOrFailInCi(referenceDb);

            using (var context = NewNoTrackingContext())
            {
                var intSql = WorldDatabase.IntRowsQuery(context, new uint[] { 273, 12, 1 }).ToSql();
                var emoteSql = WorldDatabase.EmoteRowsQuery(context, new uint[] { 273, 12, 1 }).ToSql();

                Console.WriteLine(intSql);
                Console.WriteLine(emoteSql);

                foreach (var sql in new[] { intSql, emoteSql })
                {
                    StringAssert.Contains(sql, "IN (273, 12, 1)");
                    Assert.IsFalse(sql.Contains("JSON_TABLE", StringComparison.OrdinalIgnoreCase), "the id list became a JSON_TABLE parameter join: " + sql);
                    StringAssert.Contains(sql, "ORDER BY");
                }

                StringAssert.Contains(intSql, "ORDER BY `w`.`object_Id`, `w`.`type`, `w`.`id`");
                StringAssert.Contains(emoteSql, "LEFT JOIN `weenie_properties_emote_action`");
            }
        }

        /// <summary>
        /// Up to 600 ids covering the interesting cases: creatures, books, non-creatures that carry creature-table
        /// rows, non-books that carry book rows, and ids with no weenie at all.
        /// </summary>
        private static List<uint> MixedIdSet(out List<uint> missing)
        {
            using (var context = NewNoTrackingContext())
            {
                var creatureTypes = Enum.GetValues(typeof(WeenieType)).Cast<WeenieType>().Where(WorldDatabase.IsCreatureWeenieType).Select(t => (int)t).ToList();

                var creatures = context.Weenie.Where(w => creatureTypes.Contains(w.Type)).OrderBy(w => w.ClassId).Select(w => w.ClassId).Take(200).ToList();
                var books = context.Weenie.Where(w => w.Type == (int)WeenieType.Book).OrderBy(w => w.ClassId).Select(w => w.ClassId).Take(100).ToList();
                var others = context.Weenie.Where(w => !creatureTypes.Contains(w.Type) && w.Type != (int)WeenieType.Book).OrderBy(w => w.ClassId).Select(w => w.ClassId).Take(200).ToList();

                var strayCreatureRows = (from s in context.WeeniePropertiesSkill
                                         join w in context.Weenie on s.ObjectId equals w.ClassId
                                         where !creatureTypes.Contains(w.Type)
                                         select w.ClassId).Distinct().Take(50).ToList();

                var strayBookRows = (from b in context.WeeniePropertiesBookPageData
                                     join w in context.Weenie on b.ObjectId equals w.ClassId
                                     where w.Type != (int)WeenieType.Book
                                     select w.ClassId).Distinct().Take(50).ToList();

                missing = MissingIds(3);

                return creatures.Concat(books).Concat(others).Concat(strayCreatureRows).Concat(strayBookRows).Concat(missing).Distinct().ToList();
            }
        }

        private static List<uint> MissingIds(int count)
        {
            using (var context = NewNoTrackingContext())
            {
                var max = context.Weenie.Max(w => w.ClassId);

                var ids = Enumerable.Range(1, count).Select(i => max + (uint)i * 7919).ToList();

                Assert.IsFalse(context.Weenie.Any(w => ids.Contains(w.ClassId)), "a supposedly missing id exists");

                return ids;
            }
        }
    }
}
