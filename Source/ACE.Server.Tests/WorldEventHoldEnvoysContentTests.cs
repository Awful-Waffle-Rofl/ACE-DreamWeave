using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Loads the REAL Content/events/axes folder (not a fixture) and checks the Hold goal defend_envoys shipped by
    /// World Events slice H1: it parses and validates as a Hold goal with the 1007650-1007653 wcids, it is listed by the
    /// ambush source (H6) and the web-startable envoy_muster source, and a random roll composes it only while world_events_hold_enabled is on (the H6 composer gate). Reads files only: no PropertyManager,
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

        /// <summary>
        /// Consumer pin: bots classify a client-attackable creature as a monster, so the shipped envoy knights must NOT be
        /// sent to the client as attackable. Asserts on what the knight tag and the client-Attackable core receive from the shipped goal.
        /// </summary>
        [TestMethod]
        public void DefendEnvoys_KnightsAreNotSentToTheClientAsAttackable()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());

            Assert.IsTrue(store.Goals.TryGetValue(GoalId, out var goal));
            Assert.IsFalse(goal.Protect.ShowHealthBar, "defend_envoys must not show a health bar (it makes the client treat the knights as attackable monsters)");

            var tag = WorldEventSpawner.KnightTag(1, null, goal.Protect.ShowHealthBar);

            Assert.IsFalse(WorldEventHooks.HoldClientAttackable(false, tag.Kind == HoldUnitKind.Knight, tag.ShowHealthBar));
        }

        [TestMethod]
        public void DefendEnvoys_IsListedByAmbushAndEnvoyMuster()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());

            var listing = store.Sources.Values.Where(s => s.CompatibleGoals != null && s.CompatibleGoals.Contains(GoalId))
                .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

            CollectionAssert.AreEqual(new[] { "ambush", "envoy_muster" }, listing,
                "defend_envoys must be listed by exactly ambush (in game) and envoy_muster (the web panel): listed by " + string.Join(", ", listing));
            Assert.IsFalse(store.Sources["ambush"].WebStartable, "ambush stays off the web (its 45 m edges geometry fits almost no town spot)");
            Assert.IsTrue(store.Sources["envoy_muster"].WebStartable, "envoy_muster is how the web Admin panel offers defend_envoys");
            CollectionAssert.AreEqual(new[] { GoalId }, store.Sources["envoy_muster"].CompatibleGoals.ToArray(), "envoy_muster offers Hold only");

            var mine = store.Diagnostics.Where(d => d.Contains("envoy_muster", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(0, mine.Count, "envoy_muster raised load diagnostics: " + string.Join(" | ", mine));
        }

        /// <summary>
        /// The composer gate (H6; owner ruled 2026-11-16 that random rolls may pick Hold): over the real content, a random
        /// ambush roll lands on defend_envoys for some seeds while world_events_hold_enabled is ON, and never while it is OFF.
        /// </summary>
        [TestMethod]
        public void DefendEnvoys_IsRandomlyComposed_OnlyWithHoldEnabled()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());
            var family = store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(family);

            var saved = WorldEventComposer.HoldEnabledReader;
            try
            {
                foreach (var switchOn in new[] { true, false })
                {
                    WorldEventComposer.HoldEnabledReader = _ => switchOn;

                    var composed = 0;
                    var holds = 0;

                    for (var seed = 0; seed < 60; seed++)
                    {
                        var request = WorldEventHoldComposerGateTests.Request("ambush", null, family, random: true);

                        if (!WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(seed)))
                            continue;

                        composed++;

                        if (composition.Goal.Id == GoalId)
                        {
                            holds++;

                            // A rolled Hold run rolls no boss (its finale boss is its own), exactly as a pinned start defaults.
                            Assert.AreEqual(BossKind.None, composition.Boss.Kind, $"seed {seed}: a random Hold roll drew boss {composition.Boss.Kind}");
                        }
                    }

                    var candidates = WorldEventComposer.RandomCandidatePairs(store, WorldEventHoldComposerGateTests.Request("ambush", null, family, random: true), "ambush", null);

                    if (switchOn)
                    {
                        Assert.IsTrue(candidates.Contains(("ambush", GoalId)), "switch on: defend_envoys must be a candidate pair");
                        Assert.IsTrue(composed > 0, $"switch {switchOn}: no random ambush roll composed at all, so this test proved nothing");
                        Assert.IsTrue(holds > 0, $"switch on: no random ambush roll of {composed} composed defend_envoys");
                    }
                    else
                    {
                        // The Hold filter, not the weights, must be what removes defend_envoys: assert it is absent from the
                        // pairs fed to WeightedPairOrder, then that the pool left held only weight-0 pairs (kill_count).
                        Assert.IsFalse(candidates.Contains(("ambush", GoalId)), "switch off: defend_envoys must not be a candidate pair");
                        CollectionAssert.AreEqual(new[] { ("ambush", "kill_count") }, candidates);

                        Assert.AreEqual(0, composed, $"switch off: {composed} random ambush rolls composed");
                        Assert.AreEqual(0, holds);

                        Assert.IsFalse(WorldEventComposer.TryCompose(store, catalog, WorldEventHoldComposerGateTests.Request("ambush", null, family, random: true),
                            out _, out var refusal, new Random(1)));
                        Assert.AreEqual("random composition: every candidate goal for source 'ambush' has random weight 0 " +
                                        "(pin --goal to start one explicitly; defend_envoys also needs world_events_hold_enabled)", refusal);
                    }
                }
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }

        /// <summary>
        /// Owner ruling 2026-11-16: Hold has the same likelihood as every other option. An unrestricted in-game random roll
        /// never lands on envoy_muster (inGameRandom false; ambush already carries the one defend_envoys pair), while a roll
        /// restricted to the web's allowed sources, and a pinned source, still reach it.
        /// </summary>
        [TestMethod]
        public void EnvoyMuster_IsSkippedByAnUnrestrictedRoll_ButNotByAWebRoll()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());
            var family = store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(family);

            Assert.IsFalse(store.Sources["envoy_muster"].InGameRandom);
            Assert.IsTrue(store.Sources.Values.Where(s => s.Id != "envoy_muster").All(s => s.InGameRandom), "only envoy_muster opts out");

            var saved = WorldEventComposer.HoldEnabledReader;
            try
            {
                WorldEventComposer.HoldEnabledReader = _ => true;

                var unrestricted = 0;
                var web = 0;

                for (var seed = 0; seed < 200; seed++)
                {
                    var request = WorldEventHoldComposerGateTests.Request(null, null, family, random: true);

                    if (WorldEventComposer.TryCompose(store, catalog, request, out var composition, out _, new Random(seed)))
                    {
                        unrestricted++;
                        Assert.AreNotEqual("envoy_muster", composition.Source.Id, $"seed {seed}: an unrestricted roll landed on envoy_muster");
                    }

                    var webRequest = WorldEventHoldComposerGateTests.Request(null, null, family, random: true);
                    webRequest.AllowedSourceIds = new[] { "envoy_muster" };

                    if (WorldEventComposer.TryCompose(store, catalog, webRequest, out var webComposition, out _, new Random(seed)))
                    {
                        web++;
                        Assert.AreEqual("envoy_muster", webComposition.Source.Id);
                    }
                }

                Assert.IsTrue(unrestricted > 0, "no unrestricted roll composed at all, so this test proved nothing");
                Assert.IsTrue(web > 0, "a web roll restricted to envoy_muster never composed, so the flag leaked into restricted rolls");

                var pinned = WorldEventHoldComposerGateTests.Request("envoy_muster", null, family, random: true);
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, pinned, out var pinnedComposition, out var pinnedError, new Random(1)), pinnedError);
                Assert.AreEqual("envoy_muster", pinnedComposition.Source.Id);
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