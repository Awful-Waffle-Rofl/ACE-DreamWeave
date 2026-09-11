using System.Linq;

using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// <see cref="PropertyManager.EnumerateWithPrefix"/> - the enumerator "/worldevent properties" (and any
    /// future prefix-scoped property listing) is built on, across all four Default*Properties dictionaries.
    /// </summary>
    [TestClass]
    public class PropertyManagerEnumerateTests
    {
        [TestMethod]
        public void EnumerateWithPrefix_ReturnsOnlyKeysStartingWithThePrefix()
        {
            var rows = PropertyManager.EnumerateWithPrefix("world_events_trash_band_").ToList();

            Assert.IsTrue(rows.Count > 0);
            Assert.IsTrue(rows.All(r => r.key.StartsWith("world_events_trash_band_")));
        }

        [TestMethod]
        public void EnumerateWithPrefix_IsCaseSensitiveOrdinal()
        {
            // "World_Events_" (wrong case) must match nothing - property keys are always lower_snake_case
            // and the prefix match is deliberately ordinal, not culture/case-insensitive.
            var rows = PropertyManager.EnumerateWithPrefix("World_Events_").ToList();

            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public void EnumerateWithPrefix_EmptyPrefix_ReturnsEverything()
        {
            var all = PropertyManager.EnumerateWithPrefix("").ToList();
            var worldEvents = PropertyManager.EnumerateWithPrefix("world_events_").ToList();

            Assert.IsTrue(all.Count > worldEvents.Count, "an empty prefix must return properties outside world_events_ too");
        }

        [TestMethod]
        public void EnumerateWithPrefix_SortedByKey()
        {
            var rows = PropertyManager.EnumerateWithPrefix("world_events_").Select(r => r.key).ToList();
            var sorted = rows.OrderBy(k => k, System.StringComparer.Ordinal).ToList();

            CollectionAssert.AreEqual(sorted, rows);
        }

        [TestMethod]
        public void EnumerateWithPrefix_IncludesTheLiveOverrideDials_WithZeroDefaults()
        {
            var rows = PropertyManager.EnumerateWithPrefix("world_events_").ToDictionary(r => r.key, r => r);

            // A representative sample across the long/double override dials added for the live-overrides
            // feature (TECH-DESIGN "Live overrides") - every one of these ships default 0 ("use the JSON
            // value").
            foreach (var key in new[]
            {
                "world_events_max_alive",
                "world_events_wave_count_base",
                "world_events_boss_throughput_floor_health",
                "world_events_crowd_health_cap",
                "world_events_pace_min_wave_seconds",
                "world_events_boss_per_player"
            })
            {
                Assert.IsTrue(rows.ContainsKey(key), $"expected {key} in the enumeration");
                Assert.AreEqual("0", rows[key].@default, $"{key} must default to 0 (use the JSON value)");
            }
        }

        [TestMethod]
        public void EnumerateWithPrefix_IncludesTheBandDials_WithTheirNonZeroDefaults()
        {
            var rows = PropertyManager.EnumerateWithPrefix("world_events_").ToDictionary(r => r.key, r => r);

            Assert.AreEqual("1", rows["world_events_trash_band_low"].@default);
            Assert.AreEqual("1.5", rows["world_events_trash_band_high"].@default);
            Assert.AreEqual("1.25", rows["world_events_elite_band_low"].@default);
            Assert.AreEqual("1.75", rows["world_events_elite_band_high"].@default);
        }

        [TestMethod]
        public void EnumerateWithPrefix_MarksAModifiedKey_CurrentDiffersFromDefault()
        {
            const string key = "world_events_boss_health_cap";

            try
            {
                PropertyManager.ModifyDouble(key, 42.0);

                var row = PropertyManager.EnumerateWithPrefix("world_events_").Single(r => r.key == key);

                Assert.AreEqual("0", row.@default);
                Assert.AreEqual("42", row.current);
                Assert.AreNotEqual(row.current, row.@default);
            }
            finally
            {
                // Restore - PropertyManager is a static, process-wide cache and this key's default (0) is
                // itself a valid "not overridden" state, so setting it back to 0 undoes the test's mutation
                // for every test that runs after this one in the same process.
                PropertyManager.ModifyDouble(key, 0.0);
            }
        }

        [TestMethod]
        public void EnumerateWithPrefix_UnmodifiedKey_CurrentMatchesDefault()
        {
            var row = PropertyManager.EnumerateWithPrefix("world_events_").Single(r => r.key == "world_events_synthetic_champion_health_mult");

            Assert.AreEqual(row.@default, row.current);
        }
    }
}
