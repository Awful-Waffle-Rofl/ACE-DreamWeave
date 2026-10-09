using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PropertyManager.DoWork's version-check and in-place merge steps (a third code-review round on
    /// PR #1451): SnapshotModified, VersionUnchanged, ClearModifiedIfVersionUnchanged, ShouldSkipReload
    /// and MergeReloadedInPlace, all internal (InternalsVisibleTo, ACE.Server.csproj:15) and exercised here
    /// directly against real ConcurrentDictionary&lt;string, ConfigurationEntry&lt;T&gt;&gt; instances and
    /// the real ConfigurationEntry&lt;T&gt;.Modify/ReloadFrom - not a re-implementation.
    ///
    /// DoWork's actual step 2 (the DB round trip in between) cannot be driven DB-free, so these tests
    /// simulate its effect on the cache directly: a "concurrent Modify* during the no-lock window" is
    /// simply a second Modify() call made between snapshotting and merging, exactly as it would land on
    /// another thread in production - the version counter is what makes the ORDER of those two calls
    /// relative to the DB round trip irrelevant to correctness, which is the whole point of this design.
    /// </summary>
    [TestClass]
    public class PropertyManagerVersionMergeTests
    {
        private static ConcurrentDictionary<string, ConfigurationEntry<bool>> NewCache()
        {
            return new ConcurrentDictionary<string, ConfigurationEntry<bool>>();
        }

        [TestMethod]
        public void WriteThenModifyThenMerge_KeepsTheNewerValue()
        {
            var cache = NewCache();
            cache["k"] = new ConfigurationEntry<bool>(false, false, "d0");

            // Step 1: a Modify* call, then DoWork's own step 1 snapshots it (version 1).
            cache["k"].Modify(true);
            var snapshot = PropertyManager.SnapshotModified(cache);
            Assert.AreEqual(1, snapshot.Count);
            var written = snapshot.Select(s => (s.Key, s.Version)).ToList();

            // Step 2's no-lock window: a SECOND Modify* call lands here, with a newer value and a newer
            // version - this is the exact race the review flagged, simulated by simply calling Modify
            // again before step 3 runs.
            cache["k"].Modify(false);

            // Step 3: clear-if-unchanged, using the STALE version from the first snapshot. Must NOT
            // clear, because the entry has moved on to version 2.
            PropertyManager.ClearModifiedIfVersionUnchanged(cache, written);
            Assert.IsTrue(cache["k"].Modified, "a version that moved on must stay Modified.");

            // Step 3's merge: a freshly "reloaded" row carrying the FIRST write's value (what the DB
            // round trip - simulated - actually persisted) must not clobber the newer in-memory value,
            // because the entry is still Modified.
            var reloaded = new List<(string Key, bool Value, string Description)> { ("k", true, "from-db-old-write") };
            PropertyManager.MergeReloadedInPlace(cache, reloaded);

            Assert.AreEqual(false, cache["k"].Item, "the newer value must survive the reload merge.");
            Assert.IsTrue(cache["k"].Modified, "still pending - the newer value was never actually written.");
        }

        [TestMethod]
        public void UnchangedVersion_ClearsModified()
        {
            var cache = NewCache();
            cache["k"] = new ConfigurationEntry<bool>(false, false, "d0");

            cache["k"].Modify(true);
            var snapshot = PropertyManager.SnapshotModified(cache);
            var written = snapshot.Select(s => (s.Key, s.Version)).ToList();

            // No concurrent Modify* in between - version is exactly what step 1 snapshotted.
            PropertyManager.ClearModifiedIfVersionUnchanged(cache, written);

            Assert.IsFalse(cache["k"].Modified, "an unchanged version must be cleared - the write succeeded and nothing moved it since.");
        }

        [TestMethod]
        public void Reload_DoesNotClobberAModifiedKey()
        {
            var cache = NewCache();
            cache["k"] = new ConfigurationEntry<bool>(false, false, "d0");

            // Modified, but never went through step 1/2/3 at all this cycle (e.g. it landed after this
            // cycle's snapshot already ran).
            cache["k"].Modify(true);

            var reloaded = new List<(string Key, bool Value, string Description)> { ("k", false, "from-db") };
            PropertyManager.MergeReloadedInPlace(cache, reloaded);

            Assert.AreEqual(true, cache["k"].Item, "a Modified key must never be overwritten by a reloaded DB row.");
            Assert.IsTrue(cache["k"].Modified);
        }

        [TestMethod]
        public void Reload_AppliesInPlace_WhenNotModified()
        {
            // Positive control for the two tests above: the ordinary, non-racing case must still work -
            // an unmodified entry DOES take the freshly reloaded row, and takes it on the SAME object
            // (never replaced), which is the "in place" half of the requirement.
            var cache = NewCache();
            var entry = new ConfigurationEntry<bool>(false, false, "d0");
            cache["k"] = entry;

            var reloaded = new List<(string Key, bool Value, string Description)> { ("k", true, "from-db") };
            PropertyManager.MergeReloadedInPlace(cache, reloaded);

            Assert.AreEqual(true, cache["k"].Item);
            Assert.AreEqual("from-db", cache["k"].Description);
            Assert.IsFalse(cache["k"].Modified);
            Assert.AreSame(entry, cache["k"], "the entry object must be mutated in place, never replaced.");
        }

        [TestMethod]
        public void Reload_InsertsANewKeyWhenAbsent()
        {
            var cache = NewCache();

            var reloaded = new List<(string Key, bool Value, string Description)> { ("brand_new_key", true, "from-db") };
            PropertyManager.MergeReloadedInPlace(cache, reloaded);

            Assert.IsTrue(cache.ContainsKey("brand_new_key"));
            Assert.AreEqual(true, cache["brand_new_key"].Item);
            Assert.IsFalse(cache["brand_new_key"].Modified);
        }

        [TestMethod]
        public void VersionUnchanged_NullEntry_ReturnsFalse()
        {
            Assert.IsFalse(PropertyManager.VersionUnchanged<bool>(null, 0));
        }

        [TestMethod]
        public void ShouldSkipReload_NullEntry_ReturnsFalse()
        {
            Assert.IsFalse(PropertyManager.ShouldSkipReload<bool>(null));
        }
    }
}
