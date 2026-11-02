using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Loads the REAL Content/events/axes folder (not a fixture) and checks the Hold goal defend_envoys shipped by
    /// World Events slice H1: it parses and validates as a Hold goal with the 1007650-1007653 wcids, it is listed by the
    /// ambush source (H6), and a random roll never composes it (the H6 composer gate). Reads files only: no PropertyManager,
    /// no database (world_events_hold_enabled goes through the composer's seam).
    /// </summary>
    [TestClass]
    public class WorldEventHoldEnvoysContentTests
    {
        private const string GoalId = "defend_envoys";

        private static string AxesFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "events", "axes", "goals.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/events/axes/goals.json by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Content", "events", "axes");
        }

        [TestMethod]
        public void DefendEnvoys_ParsesAsHoldGoal_WithEnvoyKnightsAndShrine()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());

            var mine = store.Diagnostics.Where(d => d.Contains(GoalId, StringComparison.Ordinal)).ToList();
            Assert.AreEqual(0, mine.Count, "defend_envoys raised load diagnostics: " + string.Join(" | ", mine));

            Assert.IsTrue(store.Goals.TryGetValue(GoalId, out var goal), "goals.json did not yield defend_envoys (rejected at load?)");
            Assert.AreEqual(GoalType.Hold, goal.TypeKind);
            Assert.IsNotNull(goal.Protect);

            CollectionAssert.AreEqual(new uint[] { 1007650, 1007651, 1007652 }, goal.Protect.Wcids.ToArray());
            Assert.AreEqual(1007650u, goal.Protect.WcidFor(0));
            Assert.IsNotNull(goal.Protect.Shrine);
            Assert.AreEqual(1007653u, goal.Protect.Shrine.Wcid);
        }

        [TestMethod]
        public void DefendEnvoys_IsListedByAmbushOnly_SoItCanBePinned()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());

            var listing = store.Sources.Values.Where(s => s.CompatibleGoals != null && s.CompatibleGoals.Contains(GoalId)).Select(s => s.Id).ToList();

            CollectionAssert.AreEqual(new[] { "ambush" }, listing,
                "defend_envoys must be listed by exactly the ambush source (H6): listed by " + string.Join(", ", listing));
            Assert.IsFalse(store.Sources["ambush"].WebStartable, "ambush is not web-startable, so the web Admin panel never offers defend_envoys");
        }

        /// <summary>
        /// The composer gate (H6, owner-pending, built pinned-only): even with world_events_hold_enabled ON, a random roll over
        /// the real content never lands on defend_envoys, while ambush itself still composes. Flipping
        /// WorldEventComposer.RandomRollIncludesHoldGoals is the one change that would make this fail.
        /// </summary>
        [TestMethod]
        public void DefendEnvoys_IsNeverRandomlyComposed_EvenWithHoldEnabled()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());
            var family = store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(family);

            var saved = WorldEventComposer.HoldEnabledReader;
            try
            {
                WorldEventComposer.HoldEnabledReader = _ => true;

                var composed = 0;

                for (var seed = 0; seed < 60; seed++)
                {
                    var request = WorldEventHoldComposerGateTests.Request("ambush", null, family, random: true);

                    if (!WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(seed)))
                        continue;

                    composed++;
                    Assert.AreNotEqual(GoalId, composition.Goal.Id, $"seed {seed}: a random roll composed defend_envoys");
                }

                Assert.IsTrue(composed > 0, "no random ambush roll composed at all, so this test proved nothing");
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }

        [TestMethod]
        public void DefendEnvoys_KnightAndShrineWeenieFilesShip_WithTheirWcids()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "Could not find Content/sql/weenies by walking up from " + AppContext.BaseDirectory);
            var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

            var expected = new (uint Wcid, string File, string ClassName, string Name, string Greeting)[]
            {
                (1007650, "1007650 Envoy Draithon.sql", "dw-envoy-draithon", "Envoy Draithon", "Hold fast. Asheron''s word must not fall here."),
                (1007651, "1007651 Envoy Donovan.sql", "dw-envoy-donovan", "Envoy Donovan", "Keep your blade between them and the message, friend."),
                (1007652, "1007652 Envoy Adarl.sql", "dw-envoy-adarl", "Envoy Adarl", "We carry Asheron''s word. Stand with us until it is safe."),
                (1007653, "1007653 Restoration Shrine.sql", "dw-restoration-shrine", null, null),
            };

            foreach (var e in expected)
            {
                var path = Path.Combine(weenies, e.File);
                Assert.IsTrue(File.Exists(path), $"{e.File} is missing from Content/sql/weenies");

                var sql = File.ReadAllText(path);
                StringAssert.Contains(sql, $"-- @owns: wcid:{e.Wcid}", e.File);
                StringAssert.Contains(sql, $"VALUES ({e.Wcid}, '{e.ClassName}'", e.File);

                if (e.Name == null)
                    continue;

                // Knights: unique display name, no retail Sir name, one Use greeting, protected-NPC flags, skills only on Adarl.
                StringAssert.Contains(sql, $"({e.Wcid},   1, '{e.Name}') /* Name */", e.File);
                Assert.IsFalse(sql.Contains("'Sir ", StringComparison.Ordinal), e.File + " still carries a retail 'Sir ' name or text");
                Assert.AreEqual(1, CountOf(sql, "INSERT INTO `weenie_properties_emote` "), e.File + " must have exactly one emote set");
                Assert.AreEqual(1, CountOf(sql, "7 /* Use */"), e.File + " must have exactly one Use-category emote");
                StringAssert.Contains(sql, "'" + e.Greeting + "'", e.File + " greeting text");
                StringAssert.Contains(sql, "19, False) /* Attackable */", e.File);
                StringAssert.Contains(sql, "52, True ) /* AiImmobile */", e.File);
                Assert.AreEqual(e.Wcid == 1007652, sql.Contains("weenie_properties_skill", StringComparison.Ordinal), e.File + ": only Sir Adarl's source has skill rows");
            }
        }

        private static int CountOf(string text, string needle)
        {
            var n = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }
    }
}