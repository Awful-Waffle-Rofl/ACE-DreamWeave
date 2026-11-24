using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Database.Tests
{
    /// <summary>
    /// ShardDatabase.RemoveCharacter evicts the static CharacterContexts cache, so a guid reused after a
    /// hard delete reads back the NEW row and not the old occupant's tracked Character object.
    ///
    /// Three layers, because the real-database test only runs on a disposable shard and is skipped in CI:
    /// the eviction helper on its own (pure, no database), a source pin that RemoveCharacter calls it after
    /// the commit (runs everywhere), and the round trip against MySQL.
    ///
    /// The round trip WRITES ROWS. It runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/> is 1,
    /// a promise that Config.js's Shard block points at a DISPOSABLE database. Its guid and account are
    /// random in high ranges per run, and its rows are deleted in a finally.
    /// </summary>
    [TestClass]
    public class CharacterContextEvictionTests
    {
        // ---------------- pure helper ----------------

        [TestMethod]
        public void Evict_removes_every_entry_with_the_id_and_nothing_else()
        {
            var table = new ConditionalWeakTable<Character, object>();
            var tombstones = new ConditionalWeakTable<Character, object>();

            var oldOccupant = new Character { Id = 0x50000010, Name = "Old" };
            var newOccupant = new Character { Id = 0x50000010, Name = "New" };
            var neighbour = new Character { Id = 0x50000011, Name = "Neighbour" };

            table.Add(oldOccupant, new object());
            table.Add(newOccupant, new object());
            table.Add(neighbour, new object());

            var evicted = ShardDatabase.EvictCharacterContexts(table, tombstones, 0x50000010);

            Assert.AreEqual(2, evicted, "both objects cached under the reused id are evicted");
            Assert.IsFalse(table.TryGetValue(oldOccupant, out _), "old occupant still cached");
            Assert.IsFalse(table.TryGetValue(newOccupant, out _), "second object under the same id still cached");
            Assert.IsTrue(table.TryGetValue(neighbour, out _), "a different id must be left alone");

            Assert.IsTrue(ShardDatabase.IsRemovedCharacter(tombstones, oldOccupant), "an evicted object is tombstoned, so a later save of it is refused");
            Assert.IsTrue(ShardDatabase.IsRemovedCharacter(tombstones, newOccupant));
            Assert.IsFalse(ShardDatabase.IsRemovedCharacter(tombstones, neighbour), "an object that was not evicted is not tombstoned");
            Assert.IsFalse(ShardDatabase.IsRemovedCharacter(tombstones, new Character { Id = 0x50000010 }), "a NEW object for the reused id is not tombstoned: the next occupant must save");
        }

        [TestMethod]
        public void Evict_of_an_uncached_id_is_a_zero_count_no_op()
        {
            var table = new ConditionalWeakTable<Character, object>();
            var tombstones = new ConditionalWeakTable<Character, object>();
            var neighbour = new Character { Id = 0x50000011 };
            table.Add(neighbour, new object());

            Assert.AreEqual(0, ShardDatabase.EvictCharacterContexts(table, tombstones, 0x50000012));
            Assert.IsTrue(table.TryGetValue(neighbour, out _));
            Assert.IsFalse(ShardDatabase.IsRemovedCharacter(tombstones, neighbour));
        }

        // ---------------- source pin ----------------

        /// <summary>
        /// CI never runs the round trip below, so this pins the wiring by source shape: RemoveCharacter evicts,
        /// and only after the delete has committed. Comments are not stripped, so the pin looks for the call
        /// as a statement ending in a semicolon, which the doc comment above the method does not contain.
        /// </summary>
        [TestMethod]
        public void RemoveCharacter_evicts_after_the_commit_under_the_write_lock()
        {
            var body = Slice("public virtual bool RemoveCharacter(uint id)", "public virtual bool RemoveCharacterFacets(");

            var writeLock = body.IndexOf("using var removalWriteLock = TryEnterCharacterRemovalWriteLock(CharacterRemovalLockTimeout);", StringComparison.Ordinal);
            var notAcquired = body.IndexOf("if (!removalWriteLock.Acquired)", StringComparison.Ordinal);
            var giveUp = body.IndexOf("return false;", notAcquired < 0 ? 0 : notAcquired, StringComparison.Ordinal);
            var context = body.IndexOf("new ShardDbContext()", StringComparison.Ordinal);
            var commit = body.IndexOf("transaction.Commit();", StringComparison.Ordinal);
            var evict = body.IndexOf("EvictCharacterContexts(CharacterContexts, RemovedCharacters, id);", StringComparison.Ordinal);
            var success = body.IndexOf("return true;", StringComparison.Ordinal);

            Assert.IsTrue(commit > 0 && context > 0, "positive control: the context and the commit must be found in RemoveCharacter");
            Assert.IsTrue(writeLock > 0 && writeLock < context, "RemoveCharacter must take the removal write lock, bounded by CharacterRemovalLockTimeout, before it opens its context");
            Assert.IsTrue(notAcquired > writeLock && giveUp > notAcquired && giveUp < context, "a lock that was not acquired must return false before anything is deleted");
            Assert.IsTrue(evict > commit, "RemoveCharacter must evict (and tombstone) after the delete commits");
            Assert.IsTrue(success > evict, "the eviction must run before RemoveCharacter reports success");
        }

        /// <summary>
        /// A reader stuck on the read side must not hang the world thread: RemoveCharacter gives up after its timeout and returns
        /// false (PurgeSlot reports that as a failed purge). Needs no database, because the lock is taken before any context exists.
        /// The timing bound is what catches a blocking EnterWriteLock: that version would wait out the helper's 5 s hold.
        /// </summary>
        [TestMethod]
        public void RemoveCharacter_gives_up_and_returns_false_when_the_removal_lock_stays_held()
        {
            var saved = ShardDatabase.CharacterRemovalLockTimeout;
            Assert.AreEqual(TimeSpan.FromSeconds(10), saved, "the production timeout is 10 s");

            using var holding = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();

            var reader = new System.Threading.Thread(() =>
            {
                ShardDatabase.EnterCharacterRemovalReadLockForTests();
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                ShardDatabase.ExitCharacterRemovalReadLockForTests();
            }) { IsBackground = true };

            ShardDatabase.CharacterRemovalLockTimeout = TimeSpan.FromMilliseconds(300);

            try
            {
                reader.Start();
                Assert.IsTrue(holding.Wait(TimeSpan.FromSeconds(5)), "the helper thread took the read side");

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var removed = new ShardDatabase().RemoveCharacter(0xE0FFFFFE);
                watch.Stop();

                Assert.IsFalse(removed, "a removal that cannot take the lock must report failure");
                Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), $"RemoveCharacter waited {watch.Elapsed.TotalSeconds:0.0} s; it must give up after its timeout, not wait for the reader");
            }
            finally
            {
                release.Set();
                reader.Join(TimeSpan.FromSeconds(10));
                ShardDatabase.CharacterRemovalLockTimeout = saved;
            }
        }

        /// <summary>Risk (b): a read that saw the row before the delete must not cache it after the eviction.</summary>
        [TestMethod]
        public void GetCharacterList_holds_the_removal_read_lock_from_its_query_to_its_cache_write()
        {
            var body = Slice("private static List<Character> GetCharacterList(", "public Character GetCharacterStubByName(");

            var readLock = body.IndexOf("using var removalReadLock = EnterCharacterRemovalReadLock();", StringComparison.Ordinal);
            var query = body.IndexOf("query.ToList()", StringComparison.Ordinal);
            var cacheWrite = body.IndexOf("CharacterContexts.Add(", StringComparison.Ordinal);

            Assert.IsTrue(query > 0 && cacheWrite > query, "positive control: the query and the cache write must be found");
            Assert.IsTrue(readLock > 0 && readLock < query, "GetCharacterList must take the removal read lock before its query (a using declaration holds it to the method's end, past the cache write)");
        }

        /// <summary>Risk (a): an evicted object saved after the removal must not re-insert the deleted row.</summary>
        [TestMethod]
        public void SaveCharacter_and_RenameCharacter_refuse_a_removed_object_on_their_insert_path()
        {
            foreach (var (start, end) in new[]
            {
                ("public bool SaveCharacter(Character character, ReaderWriterLockSlim rwLock)", "public bool AddCharacterInParallel("),
                ("public bool RenameCharacter(Character character, string newName, ReaderWriterLockSlim rwLock)", "// Proving Grounds: Speed"),
            })
            {
                var body = Slice(start, end);

                // The insert path is the SECOND context: the first branch reuses a cached context and never inserts.
                var cachedBranch = body.IndexOf("CharacterContexts.TryGetValue(character, out var cachedContext)", StringComparison.Ordinal);
                var readLock = body.IndexOf("using var removalReadLock = EnterCharacterRemovalReadLock();", StringComparison.Ordinal);
                var tombstone = body.IndexOf("if (IsRemovedCharacter(RemovedCharacters, character))", StringComparison.Ordinal);
                var insertContext = body.IndexOf("var context = new ShardDbContext();", StringComparison.Ordinal);
                var insert = body.IndexOf("context.Character.Add(character);", StringComparison.Ordinal);

                Assert.IsTrue(cachedBranch >= 0 && insertContext > cachedBranch && insert > insertContext, $"positive control: the two branches of {start} must be found");
                Assert.IsTrue(readLock > cachedBranch && readLock < tombstone, $"{start}: the removal read lock must be taken on the insert path, before the tombstone check");
                Assert.IsTrue(tombstone > 0 && tombstone < insertContext, $"{start}: the tombstone check must come before the insert path's context and INSERT");
            }
        }

        private static string Slice(string start, string end)
        {
            var src = File.ReadAllText(FindRepoFile(Path.Combine("Source", "ACE.Database", "ShardDatabase.cs")));

            var from = src.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0, $"not found: {start}");
            Assert.AreEqual(from, src.LastIndexOf(start, StringComparison.Ordinal), $"ambiguous: {start}");

            var to = src.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.IsTrue(to > from, $"the end marker '{end}' was not found after '{start}'; the slice has degenerated");

            return src.Substring(from, to - from);
        }

        // ---------------- real database ----------------

        private static readonly Random random = new Random();

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Reused_guid_reads_back_the_new_row_not_the_cached_old_one()
        {
            var db = RequireDisposableShard();

            var guid = 0xE0000000u + (uint)random.Next(0, 0x0FFFFF00);
            var account = 0xF0000000u + (uint)random.Next(0, 0x0FFFFF00);

            try
            {
                InsertRow(guid, account, $"EvictOld{guid:X8}", "evict-test-old-quest");

                // Load and HOLD the old object, so the ConditionalWeakTable entry cannot be collected out from
                // under the test and make it pass for the wrong reason.
                var held = db.GetCharacter(guid);
                Assert.IsNotNull(held, "the old row reads back");
                Assert.AreEqual("evict-test-old-quest", held.CharacterPropertiesQuestRegistry.Single().QuestName, "positive control: the old quest registry loaded");

                Assert.AreSame(held, db.GetCharacter(guid), "positive control: a second read hands back the cached object, which is the behaviour eviction must defeat");

                Assert.IsTrue(db.RemoveCharacter(guid), "RemoveCharacter");

                InsertRow(guid, account, $"EvictNew{guid:X8}", "evict-test-new-quest");

                var fresh = db.GetCharacter(guid);
                Assert.IsNotNull(fresh, "the new row reads back");
                Assert.AreNotSame(held, fresh, "GetCharacter returned the stale cached object for the reused guid");
                Assert.AreEqual($"EvictNew{guid:X8}", fresh.Name);
                CollectionAssert.AreEqual(new[] { "evict-test-new-quest" }, fresh.CharacterPropertiesQuestRegistry.Select(q => q.QuestName).ToArray(), "quest registry of the new row");

                // The account-list path goes through the same lookup and must agree.
                var listed = db.GetCharacters(account, true).Single(c => c.Id == guid);
                Assert.AreSame(fresh, listed, "GetCharacters must return the same (fresh) cached object");

                GC.KeepAlive(held);
            }
            finally
            {
                db.RemoveCharacter(guid);
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Saving_a_held_object_after_its_removal_is_refused_and_does_not_resurrect_the_row()
        {
            var db = RequireDisposableShard();

            var guid = 0xE0000000u + (uint)random.Next(0, 0x0FFFFF00);
            var account = 0xF0000000u + (uint)random.Next(0, 0x0FFFFF00);

            try
            {
                InsertRow(guid, account, $"EvictHeld{guid:X8}", "evict-test-held-quest");

                var held = db.GetCharacter(guid);
                Assert.IsNotNull(held, "the row reads back");

                Assert.IsTrue(db.RemoveCharacter(guid), "RemoveCharacter");
                Assert.AreEqual(0, RowCount(guid), "positive control: the row is gone after the removal");

                held.TotalLogins++;
                Assert.IsFalse(db.SaveCharacter(held, new System.Threading.ReaderWriterLockSlim()), "a save of the removed object must be refused");
                Assert.AreEqual(0, RowCount(guid), "the refused save must not have re-inserted the old row");

                Assert.IsFalse(db.RenameCharacter(held, $"EvictRen{guid:X8}", new System.Threading.ReaderWriterLockSlim()), "a rename of the removed object must be refused");
                Assert.AreEqual(0, RowCount(guid), "the refused rename must not have re-inserted the old row");

                GC.KeepAlive(held);
            }
            finally
            {
                db.RemoveCharacter(guid);
            }
        }

        /// <summary>
        /// Risk (b) under real concurrency: reader threads call GetCharacter in a loop while this thread removes and reinserts the
        /// character over and over. Whatever interleaving happened, the final reads must return the LAST row inserted. A single
        /// run can pass by luck, so this is meant to be run repeatedly on its own.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Concurrent_readers_never_leave_a_stale_object_behind_a_reused_guid()
        {
            var db = RequireDisposableShard();

            var guid = 0xE0000000u + (uint)random.Next(0, 0x0FFFFF00);
            var account = 0xF0000000u + (uint)random.Next(0, 0x0FFFFF00);

            const int Readers = 4;
            const int Cycles = 15;

            var stop = 0;
            var reads = 0;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var threads = new List<System.Threading.Thread>();

            // The start barrier: every reader signals after its FIRST completed read, and the cycles do not start until all have.
            // The first read pays EF's cold-start cost (model build, first connection); without the barrier the cycles could finish
            // before any reader got going, and the test would race nothing.
            using var readersRunning = new System.Threading.CountdownEvent(Readers);

            try
            {
                InsertRow(guid, account, $"Race00{guid:X8}", "race-quest-0");

                for (var t = 0; t < Readers; t++)
                {
                    var thread = new System.Threading.Thread(() =>
                    {
                        var signalled = false;

                        while (System.Threading.Volatile.Read(ref stop) == 0)
                        {
                            try
                            {
                                db.GetCharacter(guid);
                                System.Threading.Interlocked.Increment(ref reads);
                            }
                            catch (Exception ex)
                            {
                                errors.Enqueue(ex);
                            }

                            if (!signalled)
                            {
                                signalled = true;
                                readersRunning.Signal();
                            }
                        }
                    }) { IsBackground = true };

                    threads.Add(thread);
                    thread.Start();
                }

                Assert.IsTrue(readersRunning.Wait(TimeSpan.FromSeconds(60)), "the reader threads did not all complete a first read");

                var readsBefore = System.Threading.Volatile.Read(ref reads);

                for (var i = 1; i <= Cycles; i++)
                {
                    Assert.IsTrue(db.RemoveCharacter(guid), $"cycle {i}: RemoveCharacter");
                    InsertRow(guid, account, $"Race{i:00}{guid:X8}", $"race-quest-{i}");
                }

                var readsDuring = System.Threading.Volatile.Read(ref reads) - readsBefore;

                System.Threading.Volatile.Write(ref stop, 1);
                foreach (var thread in threads)
                    Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "a reader thread did not stop");

                Assert.IsTrue(errors.IsEmpty, "reader errors: " + string.Join(" | ", errors.Take(3).Select(e => e.Message)));
                Assert.IsTrue(readsDuring >= Readers, $"positive control: the readers must have read WHILE the cycles ran ({readsDuring} reads during the cycles)");

                var final = db.GetCharacter(guid);
                Assert.AreEqual($"Race{Cycles:00}{guid:X8}", final.Name, "GetCharacter returned a stale object");
                CollectionAssert.AreEqual(new[] { $"race-quest-{Cycles}" }, final.CharacterPropertiesQuestRegistry.Select(q => q.QuestName).ToArray());

                var listed = db.GetCharacters(account, true).Where(c => c.Id == guid).ToList();
                Assert.IsTrue(listed.Count >= 1 && listed.All(c => c.Name == $"Race{Cycles:00}{guid:X8}"), "GetCharacters returned a stale object: " + string.Join(", ", listed.Select(c => c.Name)));

                Console.WriteLine($"{readsDuring} concurrent reads during {Cycles} remove/reinsert cycles ({reads} in all)");
            }
            finally
            {
                System.Threading.Volatile.Write(ref stop, 1);
                foreach (var thread in threads)
                    thread.Join(TimeSpan.FromSeconds(30));

                db.RemoveCharacter(guid);
            }
        }

        private static int RowCount(uint guid)
        {
            using var context = new ShardDbContext();
            return context.Character.Count(c => c.Id == guid);
        }

        private static void InsertRow(uint guid, uint account, string name, string questName)
        {
            // A separate, disposed context: the row must arrive in the database WITHOUT passing through
            // SaveCharacter, which would itself register a fresh object in CharacterContexts and hide the bug.
            using var context = new ShardDbContext();

            var character = new Character
            {
                Id = guid,
                AccountId = account,
                Name = name,
                IsDeleted = true,
                DeleteTime = 1,
                TotalLogins = 1,
            };
            character.CharacterPropertiesQuestRegistry.Add(new CharacterPropertiesQuestRegistry { CharacterId = guid, QuestName = questName, LastTimeCompleted = 1, NumTimesCompleted = 1 });

            context.Character.Add(character);
            context.SaveChanges();
        }

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

        private static string FindRepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
        }
    }
}
