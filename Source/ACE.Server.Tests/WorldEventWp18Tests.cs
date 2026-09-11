using System;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WP-18: the four follow-ups from the first successful sky-drop live run - wave cadence on a cleared
    /// field, disc placement retry policy, the fixed reward-cache count, and the announcement prefix /
    /// chat-type resolution. Pure members only (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventWp18Tests
    {
        // ---- item 1: wave cadence ----------------------------------------------------------------------

        private const double Interval = 40d;
        private const double MinGap = WorldEvent.MinWaveGapSeconds;

        /// <summary>The pre-WP-18 behaviour, unchanged: the cadence timer alone still fires a wave.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_IntervalElapsed_WithCreaturesStillAlive_IsTrue()
        {
            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 9,
                now: 140d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null));
        }

        /// <summary>The whole point of the item: a cleared field does not wait out the remaining interval.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_FieldCleared_FiresEarly()
        {
            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 110d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null),
                "10 s in with nothing alive, the next wave should not wait another 30 s");
        }

        /// <summary>But not instantly - killing the last straggler of a wave that just landed must not chain.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_FieldClearedInsideTheMinGap_Waits()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 101d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null));

            Assert.IsTrue(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 100d + MinGap, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null),
                "the min gap is inclusive");
        }

        [TestMethod]
        public void ShouldSpawnNextWave_CreaturesAlive_BeforeTheInterval_IsFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 1,
                now: 130d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null));
        }

        /// <summary>Guard (a): the cleared-field rule must never pre-empt the opening wave.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_BeforeWave0_IsAlwaysFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, waveIndexSpawned: -1, liveCount: 0,
                now: 999d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null),
                "an empty field before wave 0 has landed is not a cleared wave");
        }

        /// <summary>Guard (d) and the objective guard: neither state may produce a wave.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_NotActiveOrObjectiveComplete_IsFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(active: false, objectiveComplete: false, 0, 0,
                now: 999d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null));

            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(active: true, objectiveComplete: true, 0, 0,
                now: 999d, lastWaveAt: 100d, interval: Interval, minGap: MinGap, remainingKills: null));
        }

        /// <summary>An interval of 0 disabled the cadence before WP-18 and still does.</summary>
        [TestMethod]
        public void ShouldSpawnNextWave_NonPositiveInterval_IsFalse()
        {
            Assert.IsFalse(WorldEvent.ShouldSpawnNextWave(true, false, 0, liveCount: 0,
                now: 999d, lastWaveAt: 100d, interval: 0d, minGap: MinGap, remainingKills: null));
        }

        [TestMethod]
        public void NextWaveTrigger_NamesTheRuleThatFired()
        {
            Assert.AreEqual("interval", WorldEvent.NextWaveTrigger(now: 140d, lastWaveAt: 100d, interval: Interval));
            Assert.AreEqual("cleared", WorldEvent.NextWaveTrigger(now: 110d, lastWaveAt: 100d, interval: Interval));
        }

        // ---- item 2: disc placement retries ------------------------------------------------------------

        /// <summary>
        /// Only a wave on a disc resamples. A disc theme's rifts and champion use the STAGE-time anchors,
        /// and resampling those would walk a rift away from the ring the run was composed around.
        /// </summary>
        [TestMethod]
        public void ResamplesOnRetry_WaveOnDiscOnly()
        {
            Assert.IsTrue(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.Wave, SourceGeometry.Disc));

            Assert.IsFalse(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.Source, SourceGeometry.Disc));
            Assert.IsFalse(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.Boss, SourceGeometry.Disc));
            Assert.IsFalse(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.Decor, SourceGeometry.Disc));
            Assert.IsFalse(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.Npc, SourceGeometry.Disc));
        }

        /// <summary>Deliberately-chosen anchors keep the jitter retry they have always had.</summary>
        [TestMethod]
        public void ResamplesOnRetry_IsFalseForEveryOtherGeometry()
        {
            foreach (var geometry in new[] { SourceGeometry.Ring, SourceGeometry.Edges, SourceGeometry.Single })
                Assert.IsFalse(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.Wave, geometry), $"{geometry}");
        }

        [TestMethod]
        public void PlacementAttemptsFor_RaisesTheBudgetOnlyWhereRetriesExploreNewGround()
        {
            Assert.AreEqual(WorldEventSpawner.PlacementAttemptsDisc,
                WorldEventSpawner.PlacementAttemptsFor(WorldEventSpawnKind.Wave, SourceGeometry.Disc));

            Assert.AreEqual(WorldEventSpawner.PlacementAttempts,
                WorldEventSpawner.PlacementAttemptsFor(WorldEventSpawnKind.Wave, SourceGeometry.Ring));

            Assert.AreEqual(WorldEventSpawner.PlacementAttempts,
                WorldEventSpawner.PlacementAttemptsFor(WorldEventSpawnKind.Source, SourceGeometry.Disc));

            Assert.IsTrue(WorldEventSpawner.PlacementAttemptsDisc > WorldEventSpawner.PlacementAttempts);
        }

        // ---- item 3: cache count -----------------------------------------------------------------------

        /// <summary>A fixed cacheCount ignores the audience entirely - one chest however many turn up.</summary>
        [TestMethod]
        public void ComputeCacheCount_FixedCount_IgnoresTheAudience()
        {
            foreach (var audience in new[] { 0, 1, 8, 40, 400 })
            {
                Assert.AreEqual(1, WorldEventRewardDelivery.ComputeCacheCount(audience, 8, 1, out var unclamped),
                    $"audience {audience}");
                Assert.AreEqual(1, unclamped);
            }
        }

        /// <summary>0 (and any axis file that omits the key) is exactly the pre-WP-18 formula.</summary>
        [TestMethod]
        public void ComputeCacheCount_ZeroCacheCount_FallsBackToParticipantsPerCache()
        {
            for (var audience = 0; audience <= 40; audience++)
            {
                Assert.AreEqual(
                    WorldEventRewardDelivery.ComputeCacheCount(audience, 8),
                    WorldEventRewardDelivery.ComputeCacheCount(audience, 8, 0, out _),
                    $"audience {audience}");
            }
        }

        [TestMethod]
        public void ComputeCacheCount_FixedCount_IsStillClampedToTheRunCeiling()
        {
            var count = WorldEventRewardDelivery.ComputeCacheCount(1, 8,
                WorldEventRewardDelivery.MaxCachesPerRun + 50, out var unclamped);

            Assert.AreEqual(WorldEventRewardDelivery.MaxCachesPerRun, count);
            Assert.AreEqual(WorldEventRewardDelivery.MaxCachesPerRun + 50, unclamped, "the clamp is still reported");
        }

        [TestMethod]
        public void ShippedReward_DeclaresExactlyOneCache()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            var store = WorldEventAxisStore.Parse(
                File.ReadAllText(Path.Combine(dir, "sources.json")),
                File.ReadAllText(Path.Combine(dir, "families.json")),
                File.ReadAllText(Path.Combine(dir, "goals.json")),
                File.ReadAllText(Path.Combine(dir, "rewards.json")),
                File.ReadAllText(Path.Combine(dir, "anchors.json")));

            Assert.AreEqual(1, store.Rewards["standard"].CacheCount);
            Assert.AreEqual(8, store.Rewards["standard"].ParticipantsPerCache, "the fallback stays in the file");
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void BadCacheCount_DropsTheRewardEntry()
        {
            foreach (var bad in new[] { "-1", "999" })
            {
                var json = @"{ ""rewards"": [ {
                    ""id"": ""standard"", ""displayName"": ""Crate"",
                    ""successCrateWcid"": 1, ""consolationCrateWcid"": 2, ""cacheWcid"": 3,
                    ""participantsPerCache"": 8, ""claimWindowSeconds"": 300,
                    ""cacheCount"": " + bad + @"
                } ] }";

                var store = WorldEventAxisStore.Parse(null, null, null, json, null);

                Assert.AreEqual(0, store.Rewards.Count, $"cacheCount {bad} must drop the entry");
                Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("cacheCount")), $"cacheCount {bad}");
            }
        }

        // ---- item 4: announcement prefix and chat type -------------------------------------------------

        /// <summary>
        /// Every global line carries the prefix exactly once. A double prefix is the specific regression
        /// risk here, because four of the five composers used to embed "World Event:" themselves.
        /// </summary>
        [TestMethod]
        public void EveryGlobalLine_CarriesThePrefixExactlyOnce()
        {
            var goal = new GoalDef { Id = "kill_count", DisplayName = "Kill Count" };

            var lines = new[]
            {
                WorldEventAnnouncer.StartLine(null, null, "the gates of Holtburg"),
                WorldEventAnnouncer.ActiveLine(null, null, goal, "the gates of Holtburg"),
                WorldEventAnnouncer.WarnLine(60),
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "x", WorldEventMvp.None, null, 0),
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, goal, "x", WorldEventMvp.None, null, 0),
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.AbortedAdmin, goal, "x", WorldEventMvp.None, null, 0)
            };

            foreach (var line in lines)
            {
                Assert.IsTrue(line.StartsWith(WorldEventAnnouncer.AnnouncePrefix, StringComparison.Ordinal),
                    $"missing prefix: {line}");

                Assert.AreEqual(1, CountOccurrences(line, WorldEventAnnouncer.AnnouncePrefix),
                    $"prefix must appear once: {line}");

                Assert.IsFalse(line.Contains("World Event:"), $"the old inline form must be gone: {line}");
            }
        }

        /// <summary>The per-wave local line is ambient flavour and deliberately carries no prefix.</summary>
        [TestMethod]
        public void WaveLine_IsNotPrefixed()
        {
            var theme = new SourceThemeDef { Id = "t", WaveFlavour = "Another rank tears its way in!" };

            Assert.AreEqual("Another rank tears its way in!", WorldEventAnnouncer.WaveLine(theme));
        }

        /// <summary>
        /// The three goal shapes the repo owner asked to have named in the final broadcast. Each already
        /// resolves through WorldEventMvpResolver; these pin that the composed Success line NAMES a player.
        /// </summary>
        [TestMethod]
        public void SuccessLine_NamesAPlayer_ForAllThreeGoalShapes()
        {
            var goal = new GoalDef { Id = "g", DisplayName = "Goal" };

            // kill_count -> MostKills. Built directly rather than through a ledger because
            // WorldEventParticipation.CreditKill takes a DamageHistoryInfo, which is an engine object (D6);
            // this is exactly the shape WorldEventMvpResolver.MostKills returns
            // (WorldEventMvpResolver.cs:42 - new WorldEventMvp(top.Name, "most kills", top.Kills)).
            var killCount = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "x",
                new WorldEventMvp("Alice", "most kills", 7), null, 3);

            Assert.IsTrue(killCount.Contains("Alice"), killCount);
            Assert.IsTrue(killCount.Contains("secured the most kills"), killCount);

            // kill_boss -> KillerPlusTopDamage with the boss wording
            var killBoss = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "x",
                WorldEventMvpResolver.KillerPlusTopDamage("Bob", null, "killing blow", "struck the killing blow, top damage: {0}"),
                null, 3);

            Assert.IsTrue(killBoss.Contains("Bob"), killBoss);
            Assert.IsTrue(killBoss.Contains("struck the final blow"), killBoss);

            // destroy_source -> KillerPlusTopDamage with the rift wording, killer and top damager differing
            var destroySource = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "x",
                WorldEventMvpResolver.KillerPlusTopDamage("Cara", "Dave", "destroyed the last rift", "destroyed the last rift, top damage: {0}"),
                null, 3);

            Assert.IsTrue(destroySource.Contains("Cara"), destroySource);
            Assert.IsTrue(destroySource.Contains("Dave"), destroySource);
            Assert.IsTrue(destroySource.Contains("destroyed the last rift"), destroySource);
        }

        /// <summary>
        /// WP-19 item 1: the repo owner picked 0x05 System (magenta) in-game over the WP-18 orange. What
        /// matters either way is that it is NOT the pale green 0x14 WorldBroadcast that blended into combat
        /// text, and not any of the three other pale-green types.
        /// </summary>
        [TestMethod]
        public void DefaultAnnounceChatType_IsMagentaSystem()
        {
            Assert.AreEqual(ChatMessageType.System, WorldEventAnnouncer.DefaultAnnounceChatType);
            Assert.AreEqual(0x05u, (uint)WorldEventAnnouncer.DefaultAnnounceChatType);

            foreach (var paleGreen in new[]
                     {
                         ChatMessageType.WorldBroadcast, ChatMessageType.Appraisal,
                         ChatMessageType.Recall, ChatMessageType.Craft, ChatMessageType.Salvaging
                     })
                Assert.AreNotEqual(paleGreen, WorldEventAnnouncer.DefaultAnnounceChatType, $"{paleGreen} is pale green");
        }

        [TestMethod]
        public void ResolveAnnounceChatType_AcceptsDefinedMembers()
        {
            var type = WorldEventAnnouncer.ResolveAnnounceChatType(0x0D, out var valid);

            Assert.IsTrue(valid);
            Assert.AreEqual(ChatMessageType.Advancement, type, "0x0D is the client's only true teal");
        }

        /// <summary>
        /// An undefined type byte is refused rather than cast blindly - the client picks its colour from
        /// that byte and an unknown one can render as nothing at all.
        /// </summary>
        [TestMethod]
        public void ResolveAnnounceChatType_RejectsUndefinedAndNegative()
        {
            foreach (var bad in new long[] { -1, 0x1A, 0x99, long.MaxValue })
            {
                var type = WorldEventAnnouncer.ResolveAnnounceChatType(bad, out var valid);

                Assert.IsFalse(valid, $"{bad} is not a defined ChatMessageType");
                Assert.AreEqual(WorldEventAnnouncer.DefaultAnnounceChatType, type, $"{bad} must fall back to orange");
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = haystack.IndexOf(needle, StringComparison.Ordinal);

            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }

            return count;
        }

        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sources.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }
    }
}
