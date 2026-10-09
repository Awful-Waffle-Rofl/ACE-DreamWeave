using System.Reflection;
using System.Threading;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PropertyManager.ConfigSync - the single lock a second code-review round introduced to replace the
    /// earlier Modified-flag merge (ShouldSkipReload alone), after that merge was found to still have two
    /// lost-write windows of its own:
    ///
    ///   (a) Write*ToDB read Item, saved it, then cleared Modified - a Modify* landing between the save
    ///       and the clear set Modified=true and then had it wiped right back by that same clear;
    ///   (b) LoadPropertiesFromDB checked Modified and then replaced the dictionary entry with a new
    ///       object - a Modify* landing between the check and the replace mutated the object that
    ///       replacement was about to discard.
    ///
    /// A THIRD review round found that round two's fix - one lock held across DoWork's whole write-then-
    /// reload span - closed both windows correctly, but meant every Modify*/Modify*Description call
    /// (reached from live in-game admin commands on the world thread, PropertyAdminService.cs:84) could
    /// stall behind a DB round trip (ShardDbContext's EnableRetryOnFailure(10)) for the length of a DB
    /// hiccup. DoWork was restructured so NO DB I/O ever runs while ConfigSync is held: it now takes the
    /// lock only to (1) snapshot Modified entries with a version stamp, and, after writing/reading the DB
    /// with no lock at all, (2) clear Modified for entries whose version has not moved and merge reloaded
    /// rows in place - see PropertyManagerVersionMergeTests for that in-memory version/merge logic proven
    /// DB-free. A FOURTH review round found the same shape still in Initialize's own one-time load (its DB
    /// read left under the lock on a since-removed boot-order argument); Initialize now follows the same
    /// read-with-no-lock-then-merge-under-the-lock shape, via the same MergeReloadedInPlace, so the
    /// invariant is unconditionally true rather than depending on when in boot a caller runs. ConfigSync
    /// itself is unchanged in shape throughout all four rounds: every Modify*/Modify*Description call
    /// still takes it for its own in-memory mutation, which is exactly what these two tests prove.
    ///
    /// DoWork's and Initialize's real DB reads/writes cannot be driven DB-free (WriteBoolSnapshotToDB and
    /// the ShardConfig.GetAll* reads need a live ShardConfig DB connection this test process does not have
    /// - see PropertyManager reads throw in unit tests). What CAN be proven DB-free, and is the actual
    /// mechanism the guarantee rests on, is that PropertyManager.ModifyBool/ModifyLong genuinely block on
    /// the exact same lock object DoWork's snapshot/merge steps take: these tests take that lock directly
    /// via reflection (ConfigSync is a private static field) to stand in for "DoWork's snapshot-or-merge
    /// step is in progress", and confirm a concurrent Modify* call blocks until it is released and then
    /// completes with the new value intact - i.e. a Modify* landing during that window is delayed, not
    /// lost.
    /// </summary>
    [TestClass]
    public class PropertyManagerConfigSyncTests
    {
        private static object GetConfigSync()
        {
            var field = typeof(PropertyManager).GetField("ConfigSync", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                throw new System.InvalidOperationException("PropertyManager.ConfigSync was not found by reflection - has it been renamed?");

            return field.GetValue(null);
        }

        [TestMethod]
        public void ModifyBool_BlocksWhileConfigSyncIsHeld_ThenAppliesOnRelease()
        {
            var configSync = GetConfigSync();

            // "chat_log_audit" carries no side-effect hook (unlike "world_closed"), so this test exercises
            // pure lock behavior with nothing else in play. It is a real DefaultBooleanProperties key, so
            // ModifyBool actually takes the lock rather than early-returning.
            using var seed = PropertyCacheSeed.Bool("chat_log_audit", false);

            var modifyStarted = new ManualResetEventSlim(false);
            var modifyCompleted = new ManualResetEventSlim(false);

            Thread worker = null;

            Monitor.Enter(configSync);
            try
            {
                worker = new Thread(() =>
                {
                    modifyStarted.Set();
                    PropertyManager.ModifyBool("chat_log_audit", true);
                    modifyCompleted.Set();
                });
                worker.IsBackground = true;
                worker.Start();

                Assert.IsTrue(modifyStarted.Wait(2000), "the worker thread never started");

                // The worker is now blocked trying to enter ConfigSync (held by this thread). Give it a
                // real chance to have run to completion if the lock were NOT being respected. Do NOT join
                // the worker here - it cannot finish until the lock below is released, so joining inside
                // this block would just block this thread for the full timeout with the lock still held.
                Assert.IsFalse(modifyCompleted.Wait(300),
                    "ModifyBool completed while ConfigSync was still held - it is not taking the lock.");
            }
            finally
            {
                Monitor.Exit(configSync);
            }

            Assert.IsTrue(modifyCompleted.Wait(2000), "ModifyBool never completed after ConfigSync was released.");
            worker.Join(2000);
            Assert.IsFalse(worker.IsAlive, "worker thread did not finish joining");
            Assert.IsTrue(PropertyManager.GetBool("chat_log_audit", false).Item,
                "the value set while blocked must still land once the lock was released - not lost.");
        }

        [TestMethod]
        public void ModifyLong_BlocksWhileConfigSyncIsHeld_ThenAppliesOnRelease()
        {
            // Same proof for a second property type, confirming the lock is shared across types, not
            // just reached by ModifyBool.
            var configSync = GetConfigSync();

            const string key = "char_delete_time";
            const long compiledDefault = 3600;

            var modifyStarted = new ManualResetEventSlim(false);
            var modifyCompleted = new ManualResetEventSlim(false);

            Thread worker = null;

            Monitor.Enter(configSync);
            try
            {
                worker = new Thread(() =>
                {
                    modifyStarted.Set();
                    PropertyManager.ModifyLong(key, 42);
                    modifyCompleted.Set();
                });
                worker.IsBackground = true;
                worker.Start();

                Assert.IsTrue(modifyStarted.Wait(2000), "the worker thread never started");

                // See ModifyBool_BlocksWhileConfigSyncIsHeld_ThenAppliesOnRelease for why the worker is
                // not joined here - it cannot finish until the lock below is released.
                Assert.IsFalse(modifyCompleted.Wait(300),
                    "ModifyLong completed while ConfigSync was still held - it is not taking the lock.");
            }
            finally
            {
                Monitor.Exit(configSync);
            }

            try
            {
                Assert.IsTrue(modifyCompleted.Wait(2000), "ModifyLong never completed after ConfigSync was released.");
                worker.Join(2000);
                Assert.IsFalse(worker.IsAlive, "worker thread did not finish joining");
                Assert.AreEqual(42, PropertyManager.GetLong(key, 0).Item,
                    "the value set while blocked must still land once the lock was released - not lost.");
            }
            finally
            {
                PropertyManager.ModifyLong(key, compiledDefault);
            }
        }
    }
}
