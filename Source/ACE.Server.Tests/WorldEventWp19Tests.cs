using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WP-19: the runtime-selectable reward chest and the treasure dressing around it. Pure members only
    /// (TECH-DESIGN D6) - nothing here builds a WorldObject, a landblock or a session.
    /// </summary>
    [TestClass]
    public class WorldEventWp19Tests
    {
        // Same outdoor cell shape the geometry, decor and npc tests use: cell bits below 0x100 (outdoors)
        // and non-zero, so the Position constructor does not rebase X/Y through SetPosition.
        private const uint OutdoorCell = 0x016C0025;

        private const float CacheX = 96f;
        private const float CacheY = 96f;
        private const float CacheZ = 42f;

        private const uint TestInstance = 7u;

        private static Position CachePosition()
        {
            return new Position(OutdoorCell, CacheX, CacheY, CacheZ, 0f, 0f, 0f, 1f, TestInstance);
        }

        // ---- item 2: cache wcid override ---------------------------------------------------------------

        [TestMethod]
        public void ResolveCacheWcid_NoOverride_UsesTheProfile()
        {
            Assert.AreEqual(1002602u,
                WorldEventRewardDelivery.ResolveCacheWcid(1002602u, 0u, overrideResolves: false));

            Assert.AreEqual(1002602u,
                WorldEventRewardDelivery.ResolveCacheWcid(1002602u, 0u, overrideResolves: true),
                "an override of 0 means 'use the profile' whatever the resolve flag says");
        }

        [TestMethod]
        public void ResolveCacheWcid_ResolvingOverride_Wins()
        {
            Assert.AreEqual(999999u,
                WorldEventRewardDelivery.ResolveCacheWcid(1002602u, 999999u, overrideResolves: true));
        }

        /// <summary>
        /// The load-bearing half: an override naming a wcid that does not exist must fall back rather than
        /// spawn nothing, because a run that places no cache pays nobody.
        /// </summary>
        [TestMethod]
        public void ResolveCacheWcid_NonResolvingOverride_FallsBackToTheProfile()
        {
            Assert.AreEqual(1002602u,
                WorldEventRewardDelivery.ResolveCacheWcid(1002602u, 424242u, overrideResolves: false));
        }

        /// <summary>The helper is wcid-agnostic: no chest weenie is special-cased anywhere in this path.</summary>
        [TestMethod]
        public void ResolveCacheWcid_WorksForAnyWcid()
        {
            foreach (var wcid in new uint[] { 1u, 12345u, 1002602u, uint.MaxValue })
                Assert.AreEqual(wcid, WorldEventRewardDelivery.ResolveCacheWcid(7u, wcid, overrideResolves: true), $"wcid {wcid}");
        }

        // ---- cache pool ----------------------------------------------------------------------------------

        /// <summary>A resolving override still wins outright over the pool.</summary>
        [TestMethod]
        public void ResolveCacheWcidPool_ResolvingOverride_WinsOverThePool()
        {
            var called = false;

            var result = WorldEventRewardDelivery.ResolveCacheWcid(1002602u,
                new List<uint> { 1002633u, 1002634u }, 999999u, overrideResolves: true,
                pickIndex: n => { called = true; return 0; });

            Assert.AreEqual(999999u, result);
            Assert.IsFalse(called, "pickIndex must not be consulted when the override wins");
        }

        /// <summary>No override, non-empty pool: the pick comes from pickIndex, called with the pool's count.</summary>
        [TestMethod]
        public void ResolveCacheWcidPool_NoOverride_PicksFromThePool()
        {
            var pool = new List<uint> { 1002633u, 1002634u, 1002635u };
            var seenCount = -1;

            var result = WorldEventRewardDelivery.ResolveCacheWcid(1002602u, pool, 0u, overrideResolves: false,
                pickIndex: n => { seenCount = n; return 2; });

            Assert.AreEqual(3, seenCount, "pickIndex must be called with the pool's count");
            Assert.AreEqual(1002635u, result);
        }

        /// <summary>An empty pool falls back to the profile wcid, exactly like the pre-pool behaviour.</summary>
        [TestMethod]
        public void ResolveCacheWcidPool_EmptyPool_FallsBackToProfile()
        {
            var result = WorldEventRewardDelivery.ResolveCacheWcid(1002602u, new List<uint>(), 0u,
                overrideResolves: false, pickIndex: n => throw new InvalidOperationException("must not be called"));

            Assert.AreEqual(1002602u, result);
        }

        /// <summary>The 3-arg overload existing callers/tests use keeps compiling and behaving unchanged.</summary>
        [TestMethod]
        public void ResolveCacheWcid_ThreeArgOverload_StillWorks()
        {
            Assert.AreEqual(1002602u, WorldEventRewardDelivery.ResolveCacheWcid(1002602u, 0u, overrideResolves: false));
            Assert.AreEqual(999999u, WorldEventRewardDelivery.ResolveCacheWcid(1002602u, 999999u, overrideResolves: true));
        }

        // ---- the pool is a Success-only reward -----------------------------------------------------------
        //
        // ResolveCacheWcid itself has no notion of run outcome (see its remarks) - the gate lives at the
        // SpawnCaches call site, which passes an EMPTY pool whenever evt.Outcome != Success. These three
        // tests model exactly that call-site behaviour with a plain local helper, so a regression that wires
        // the pool into a failed run's resolve call fails here rather than only in a live server.

        private static uint ResolveCacheWcidForOutcome(WorldEventOutcome outcome, uint profileWcid,
            IReadOnlyList<uint> pool, uint overrideWcid, bool overrideResolves, Func<int, int> pickIndex)
        {
            var effectivePool = outcome == WorldEventOutcome.Success ? pool : Array.Empty<uint>();

            return WorldEventRewardDelivery.ResolveCacheWcid(profileWcid, effectivePool, overrideWcid,
                overrideResolves, pickIndex);
        }

        [TestMethod]
        public void ResolveCacheWcidForOutcome_Success_PicksFromThePool()
        {
            var pool = new List<uint> { 1002633u, 1002634u, 1002635u };

            var result = ResolveCacheWcidForOutcome(WorldEventOutcome.Success, 1002602u, pool, 0u,
                overrideResolves: false, pickIndex: n => 1);

            Assert.AreEqual(1002634u, result);
        }

        [TestMethod]
        public void ResolveCacheWcidForOutcome_Failed_FallsBackToProfileEvenWithAPool()
        {
            var pool = new List<uint> { 1002633u, 1002634u, 1002635u };

            foreach (var outcome in new[]
                     {
                         WorldEventOutcome.FailedTimeout, WorldEventOutcome.FailedWipe,
                         WorldEventOutcome.FailedNoParticipants
                     })
            {
                var result = ResolveCacheWcidForOutcome(outcome, 1002602u, pool, 0u, overrideResolves: false,
                    pickIndex: n => throw new InvalidOperationException($"pool must not be consulted on {outcome}"));

                Assert.AreEqual(1002602u, result, $"outcome {outcome}");
            }
        }

        [TestMethod]
        public void ResolveCacheWcidForOutcome_Failed_ResolvingOverrideStillWins()
        {
            var pool = new List<uint> { 1002633u, 1002634u, 1002635u };

            var result = ResolveCacheWcidForOutcome(WorldEventOutcome.FailedWipe, 1002602u, pool, 999999u,
                overrideResolves: true, pickIndex: n => throw new InvalidOperationException("pool must not be consulted"));

            Assert.AreEqual(999999u, result, "the override is a test knob and wins on either outcome");
        }

        // ---- item 3: DecorDef dx/dy --------------------------------------------------------------------

        /// <summary>
        /// The compatibility guarantee for adding Dx/Dy to the shared DecorDef, rescoped (WP-21) to the two
        /// themes it was actually written for: the Sky Rift stack and the Weave Spiral rigs must still read
        /// 0 on both, because neither declares the keys. The element_portal_* themes added by WP-21 DO
        /// declare a non-zero dy on purpose (see ElementPortalThemes_DecorIsPitchedUprightWithFiniteOffsets
        /// below), so they are deliberately excluded from this pin rather than making it pass by widening it.
        /// </summary>
        [TestMethod]
        public void ShippedDecor_HasZeroDxAndDy()
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

            var zeroOffsetThemeIds = new[] { "sky_rift", "weave_spiral" };
            var checkedEntries = 0;

            foreach (var themeId in zeroOffsetThemeIds)
            {
                Assert.IsTrue(store.Sources.ContainsKey(themeId), $"expected theme '{themeId}' to be shipped");

                foreach (var decor in store.Sources[themeId].Decor)
                {
                    Assert.AreEqual(0f, decor.Dx, 0.0001f, $"{themeId} decor wcid {decor.Wcid} dx");
                    Assert.AreEqual(0f, decor.Dy, 0.0001f, $"{themeId} decor wcid {decor.Wcid} dy");
                    checkedEntries++;
                }
            }

            Assert.IsTrue(checkedEntries >= 9, $"expected the shipped decor entries to be covered, saw {checkedEntries}");
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        /// <summary>
        /// WP-21: every element_portal_* theme's decor stands its disc UPRIGHT (pitch 90, see
        /// WorldEventSpawner.DecorRotation's remarks) and carries a non-zero, but finite, dy offset - the
        /// opposite shape from ShippedDecor_HasZeroDxAndDy above, pinned separately so a future edit that
        /// zeroes an element_portal offset (or drops its pitch) fails a test named for what it broke.
        /// </summary>
        [TestMethod]
        public void ElementPortalThemes_DecorIsPitchedUprightWithFiniteOffsets()
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

            var elementPortalThemeIds = store.Sources.Keys
                .Where(id => id.StartsWith("element_portal_", StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(5, elementPortalThemeIds.Count,
                "expected all five element_portal_* themes (fire, acid, frost, lightning, earth)");

            var checkedEntries = 0;

            foreach (var themeId in elementPortalThemeIds)
            {
                var decor = store.Sources[themeId].Decor;

                Assert.IsTrue(decor.Count > 0, $"{themeId} must declare decor");

                // Round 2 (2026-08-16): each theme's decor is FIVE pitched portal layers (the Sky Rift
                // ladder, stood upright) plus FOUR terrain-snapped ground scenery columns (pitch 0).
                var layers = decor.Where(e => e.Pitch > 0f).ToList();
                var scenery = decor.Where(e => e.Pitch == 0f).ToList();

                Assert.AreEqual(5, layers.Count, $"{themeId} portal layers");
                Assert.AreEqual(4, scenery.Count, $"{themeId} scenery columns");
                Assert.IsTrue(scenery.All(e => e.Snap), $"{themeId} scenery must be terrain-snapped");
                Assert.IsTrue(layers.All(e => !e.Snap), $"{themeId} portal layers must not snap");

                foreach (var entry in layers)
                    Assert.AreEqual(90f, entry.Pitch, 0.0001f, $"{themeId} decor wcid {entry.Wcid} pitch");

                // Smaller layers spin faster and step 0.2 m apart so no two share a plane.
                var ordered = layers.OrderByDescending(e => e.Scale).ToList();

                for (var i = 1; i < ordered.Count; i++)
                    Assert.IsTrue(ordered[i].Speed > ordered[i - 1].Speed, $"{themeId} layer {i} must spin faster than layer {i - 1}");

                var planes = ordered.Select(e => e.Dy - 0.455f * e.Scale).ToList();

                for (var i = 1; i < planes.Count; i++)
                    Assert.IsTrue(Math.Abs(planes[i] - planes[i - 1]) > 0.1f, $"{themeId} layers {i - 1}/{i} share a plane");

                foreach (var entry in decor)
                {
                    Assert.IsTrue(float.IsFinite(entry.Dx), $"{themeId} decor wcid {entry.Wcid} dx must be finite");
                    Assert.IsTrue(float.IsFinite(entry.Dy), $"{themeId} decor wcid {entry.Wcid} dy must be finite");
                    checkedEntries++;
                }
            }

            Assert.IsTrue(checkedEntries >= 45, $"expected the five element_portal_* themes' decor entries to be covered, saw {checkedEntries}");
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void DecorDef_DxAndDy_DefaultToZero()
        {
            var def = new DecorDef();

            Assert.AreEqual(0f, def.Dx, 0.0001f);
            Assert.AreEqual(0f, def.Dy, 0.0001f);
        }

        // ---- item 3: cacheDecor round-trip and validation -----------------------------------------------

        /// <summary>
        /// The shipped profile's reward-site dressing: six props ringing the cache at the geometry centre -
        /// four gold hoard mounds (1002630/1002631) and two crystal clusters (1002632), all on the ground
        /// (dz 0) and upright. Pinned here because the arrangement is a look decision, so a silent edit to
        /// the offsets or the wcids should fail a test rather than only show up in-game.
        /// </summary>
        [TestMethod]
        public void ShippedReward_CarriesTheDressingSet()
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

            var dressing = store.Rewards["standard"].CacheDecor;

            Assert.IsNotNull(dressing, "a missing cacheDecor key must never produce null");
            Assert.AreEqual(6, dressing.Count);

            CollectionAssert.AreEqual(
                new uint[] { 1002630, 1002631, 1002630, 1002631, 1002632, 1002632 },
                dressing.Select(d => d.Wcid).ToArray());

            foreach (var prop in dressing)
            {
                Assert.AreEqual(0f, prop.Dz, 0.0001f, "dressing sits on the ground; the spawner snaps to terrain");
                Assert.IsFalse(prop.Inverted, "no dressing prop is flipped");
                Assert.IsTrue(prop.Scale > 0f);
                Assert.IsTrue(prop.Speed > 0f);

                // Every prop rings the chest rather than standing on top of it, and stays inside the radius
                // a player can take in at a glance.
                var radius = Math.Sqrt(prop.Dx * prop.Dx + prop.Dy * prop.Dy);

                Assert.IsTrue(radius > 3.0 && radius < 5.5, $"prop {prop.Wcid} sits {radius:F2} m from the cache");
            }

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void CacheDecor_RoundTripsEveryField()
        {
            const string json = @"{ ""rewards"": [ {
                ""id"": ""standard"", ""displayName"": ""Crate"",
                ""successCrateWcid"": 1, ""consolationCrateWcid"": 2, ""cacheWcid"": 3,
                ""participantsPerCache"": 8, ""claimWindowSeconds"": 300, ""cacheCount"": 1,
                ""cacheDecor"": [
                  { ""wcid"": 4242, ""dx"": 1.5, ""dy"": -2.25, ""dz"": 0.1, ""scale"": 1.4, ""speed"": 1.0, ""yaw"": 45.0, ""inverted"": false }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(null, null, null, json, null);

            var decor = store.Rewards["standard"].CacheDecor;

            Assert.AreEqual(1, decor.Count);
            Assert.AreEqual(4242u, decor[0].Wcid);
            Assert.AreEqual(1.5f, decor[0].Dx, 0.0001f);
            Assert.AreEqual(-2.25f, decor[0].Dy, 0.0001f);
            Assert.AreEqual(0.1f, decor[0].Dz, 0.0001f);
            Assert.AreEqual(1.4f, decor[0].Scale, 0.0001f);
            Assert.AreEqual(1.0f, decor[0].Speed, 0.0001f);
            Assert.AreEqual(45.0f, decor[0].Yaw, 0.0001f);
            Assert.IsFalse(decor[0].Inverted);

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        /// <summary>A bad prop is dressing that failed: it drops alone and the reward profile survives.</summary>
        [TestMethod]
        public void BadCacheDecorEntry_IsDropped_RewardKept()
        {
            const string json = @"{ ""rewards"": [ {
                ""id"": ""standard"", ""displayName"": ""Crate"",
                ""successCrateWcid"": 1, ""consolationCrateWcid"": 2, ""cacheWcid"": 3,
                ""participantsPerCache"": 8, ""claimWindowSeconds"": 300,
                ""cacheDecor"": [
                  { ""wcid"": 0,    ""dx"": 1.0, ""dy"": 0.0, ""dz"": 0.0, ""scale"": 1.0, ""speed"": 1.0, ""yaw"": 0.0 },
                  { ""wcid"": 4242, ""dx"": 1.0, ""dy"": 0.0, ""dz"": 0.0, ""scale"": 0.0, ""speed"": 1.0, ""yaw"": 0.0 },
                  { ""wcid"": 4243, ""dx"": 1.0, ""dy"": 0.0, ""dz"": 0.0, ""scale"": 1.0, ""speed"": 0.0, ""yaw"": 0.0 },
                  { ""wcid"": 4244, ""dx"": ""NaN"", ""dy"": 0.0, ""dz"": 0.0, ""scale"": 1.0, ""speed"": 1.0, ""yaw"": 0.0 },
                  { ""wcid"": 4245, ""dx"": 2.0, ""dy"": 3.0, ""dz"": 0.0, ""scale"": 1.0, ""speed"": 1.0, ""yaw"": 90.0 }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(null, null, null, json, null);

            Assert.IsTrue(store.Rewards.ContainsKey("standard"), "a reward with a bad prop must be KEPT");

            var decor = store.Rewards["standard"].CacheDecor;

            Assert.AreEqual(1, decor.Count, "only the valid entry survives");
            Assert.AreEqual(4245u, decor[0].Wcid);

            Assert.AreEqual(4, store.Diagnostics.Count(d => d.Contains("standard") && d.Contains("cacheDecor")),
                "one diagnostic per dropped entry");
        }

        [TestMethod]
        public void CacheDecor_OmittedOrNull_IsAnEmptyList()
        {
            const string omitted = @"{ ""rewards"": [ {
                ""id"": ""a"", ""displayName"": ""A"", ""successCrateWcid"": 1, ""consolationCrateWcid"": 2,
                ""cacheWcid"": 3, ""participantsPerCache"": 8, ""claimWindowSeconds"": 300
            } ] }";

            const string nulled = @"{ ""rewards"": [ {
                ""id"": ""a"", ""displayName"": ""A"", ""successCrateWcid"": 1, ""consolationCrateWcid"": 2,
                ""cacheWcid"": 3, ""participantsPerCache"": 8, ""claimWindowSeconds"": 300,
                ""cacheDecor"": null
            } ] }";

            foreach (var json in new[] { omitted, nulled })
            {
                var store = WorldEventAxisStore.Parse(null, null, null, json, null);

                Assert.IsNotNull(store.Rewards["a"].CacheDecor);
                Assert.AreEqual(0, store.Rewards["a"].CacheDecor.Count);
            }
        }

        // ---- item 3: placement helper ------------------------------------------------------------------

        [TestMethod]
        public void CacheDecorPosition_AppliesTheOffsetAroundTheCache()
        {
            var cache = CachePosition();

            var position = WorldEventRewardDelivery.CacheDecorPosition(cache, 2f, -3f, false, 0f);

            Assert.AreEqual(CacheX + 2f, position.PositionX, 0.0001f);
            Assert.AreEqual(CacheY - 3f, position.PositionY, 0.0001f);
            Assert.AreEqual(CacheZ, position.PositionZ, 0.0001f, "Z is left for the caller to snap to terrain");
            Assert.AreEqual(cache.Instance, position.Instance, "the instance must follow the cache");
            Assert.AreEqual(cache.InstancedLandblock, position.InstancedLandblock);
        }

        [TestMethod]
        public void CacheDecorPosition_ZeroOffset_SitsOnTheCache()
        {
            var position = WorldEventRewardDelivery.CacheDecorPosition(CachePosition(), 0f, 0f, false, 0f);

            Assert.AreEqual(CacheX, position.PositionX, 0.0001f);
            Assert.AreEqual(CacheY, position.PositionY, 0.0001f);
        }

        /// <summary>Rotation composes exactly as spawn-time decor does - the same helper, so yaw 0 upright is identity.</summary>
        [TestMethod]
        public void CacheDecorPosition_UsesTheDecorRotation()
        {
            var upright = WorldEventRewardDelivery.CacheDecorPosition(CachePosition(), 0f, 0f, false, 90f);
            Assert.AreEqual(WorldEventSpawner.DecorRotation(false, 90f), upright.Rotation);

            var inverted = WorldEventRewardDelivery.CacheDecorPosition(CachePosition(), 0f, 0f, true, 45f);
            Assert.AreEqual(WorldEventSpawner.DecorRotation(true, 45f), inverted.Rotation);
        }

        [TestMethod]
        public void CacheDecorPosition_DoesNotMutateTheCachePosition()
        {
            var cache = CachePosition();

            WorldEventRewardDelivery.CacheDecorPosition(cache, 5f, 5f, true, 180f);

            Assert.AreEqual(CacheX, cache.PositionX, 0.0001f, "the cache position must be copied, not moved");
            Assert.AreEqual(CacheY, cache.PositionY, 0.0001f);
            Assert.AreEqual(OutdoorCell, cache.Cell, "the cache cell must not be rebased");
        }

        /// <summary>
        /// The reason this uses SetPosition rather than a raw coordinate write: a few metres of offset can
        /// cross a 24 m land cell boundary, and the cell has to be re-resolved. The LANDBLOCK is unchanged.
        /// </summary>
        [TestMethod]
        public void CacheDecorPosition_ReResolvesTheLandCell_WhenTheOffsetCrossesOne()
        {
            var cache = CachePosition();

            var position = WorldEventRewardDelivery.CacheDecorPosition(cache, -2.4f, 0f, false, 0f);

            Assert.AreNotEqual(cache.Cell, position.Cell, "an offset across a cell boundary must re-resolve the cell");
            Assert.AreEqual(cache.InstancedLandblock, position.InstancedLandblock, "but it is still the same landblock");
        }

        [TestMethod]
        public void CacheDecorPosition_NullCache_ReturnsNull()
        {
            Assert.IsNull(WorldEventRewardDelivery.CacheDecorPosition(null, 1f, 1f, false, 0f));
        }

        // ---- helpers -----------------------------------------------------------------------------------

        /// <summary>Axis-store validation: 0 entries are dropped with a diagnostic, duplicates de-duplicated
        /// silently, and the reward entry itself is always kept.</summary>
        [TestMethod]
        public void ParseRewards_CacheWcidsDropsZerosAndDedupes()
        {
            var rewardsJson = @"{
              ""rewards"": [
                {
                  ""id"": ""standard"",
                  ""displayName"": ""Test Crate"",
                  ""successCrateWcid"": 1,
                  ""consolationCrateWcid"": 2,
                  ""cacheWcid"": 1002602,
                  ""cacheWcids"": [0, 5, 5],
                  ""participantsPerCache"": 8,
                  ""claimWindowSeconds"": 300
                }
              ]
            }";

            var store = WorldEventAxisStore.Parse(null, null, null, rewardsJson, null);

            Assert.IsTrue(store.Rewards.ContainsKey("standard"), "the reward entry must still be kept");

            CollectionAssert.AreEqual(new uint[] { 5u }, store.Rewards["standard"].CacheWcids.ToArray());

            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("cacheWcids contains 0")),
                $"expected a diagnostic about the dropped 0 entry, saw: {string.Join(" | ", store.Diagnostics)}");
        }

        /// <summary>The shipped standard profile's pool is exactly the six alternate chest looks (2026-08-16).</summary>
        [TestMethod]
        public void ShippedRewards_StandardProfile_HasTheSixCacheLooks()
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

            Assert.IsTrue(store.Rewards.ContainsKey("standard"), "expected the 'standard' reward profile to be shipped");

            var expected = new uint[] { 1002633u, 1002634u, 1002635u, 1002636u, 1002629u, 1002637u };

            CollectionAssert.AreEqual(expected, store.Rewards["standard"].CacheWcids.ToArray());
        }

        // ---- WP-22: cache effect script resolution ---------------------------------------------------

        [TestMethod]
        public void ResolveCacheEffectScript_Default_ResolvesToWeddingBliss()
        {
            var script = WorldEventRewardDelivery.ResolveCacheEffectScript(
                WorldEventRewardDelivery.DefaultCacheEffectScript, out var valid);

            Assert.IsTrue(valid);
            Assert.AreEqual(PlayScript.WeddingBliss, script);
        }

        [TestMethod]
        public void ResolveCacheEffectScript_Zero_Disables()
        {
            var script = WorldEventRewardDelivery.ResolveCacheEffectScript(0, out var valid);

            Assert.IsTrue(valid, "0 is a deliberate off switch, not a bad value");
            Assert.AreEqual(PlayScript.Invalid, script);
        }

        [TestMethod]
        public void ResolveCacheEffectScript_UndefinedValue_FallsBackAndReportsInvalid()
        {
            var script = WorldEventRewardDelivery.ResolveCacheEffectScript(0xFFFFFF, out var valid);

            Assert.IsFalse(valid);
            Assert.AreEqual(PlayScript.WeddingBliss, script);
        }

        [TestMethod]
        public void ResolveCacheEffectScript_NegativeValue_FallsBackAndReportsInvalid()
        {
            var script = WorldEventRewardDelivery.ResolveCacheEffectScript(-1, out var valid);

            Assert.IsFalse(valid);
            Assert.AreEqual(PlayScript.WeddingBliss, script);
        }

        [TestMethod]
        public void ResolveCacheEffectScript_AnyDefinedValue_IsAccepted()
        {
            foreach (PlayScript expected in new[] { PlayScript.LevelUp, PlayScript.EnchantUpWhite, PlayScript.WeddingBliss })
            {
                var script = WorldEventRewardDelivery.ResolveCacheEffectScript((long)expected, out var valid);

                Assert.IsTrue(valid, $"{expected} should resolve as valid");
                Assert.AreEqual(expected, script);
            }
        }

        // ---- WP-22: repeat cadence ----------------------------------------------------------------------

        [TestMethod]
        public void ShouldReplay_RepeatZero_NeverReplays()
        {
            Assert.IsFalse(WorldEventRewardDelivery.ShouldReplay(now: 1000, claimWindowEndsAt: 2000, lastPlayedAt: 0, repeatSeconds: 0));
        }

        [TestMethod]
        public void ShouldReplay_RepeatNegative_NeverReplays()
        {
            Assert.IsFalse(WorldEventRewardDelivery.ShouldReplay(now: 1000, claimWindowEndsAt: 2000, lastPlayedAt: 0, repeatSeconds: -5));
        }

        [TestMethod]
        public void ShouldReplay_BeforeCadence_DoesNotReplay()
        {
            // Last played at 100, repeat 30: not due until 130.
            Assert.IsFalse(WorldEventRewardDelivery.ShouldReplay(now: 129, claimWindowEndsAt: 0, lastPlayedAt: 100, repeatSeconds: 30));
        }

        [TestMethod]
        public void ShouldReplay_AtCadence_Replays()
        {
            Assert.IsTrue(WorldEventRewardDelivery.ShouldReplay(now: 130, claimWindowEndsAt: 0, lastPlayedAt: 100, repeatSeconds: 30));
        }

        [TestMethod]
        public void ShouldReplay_PastCadence_Replays()
        {
            Assert.IsTrue(WorldEventRewardDelivery.ShouldReplay(now: 500, claimWindowEndsAt: 0, lastPlayedAt: 100, repeatSeconds: 30));
        }

        [TestMethod]
        public void ShouldReplay_WindowNotSet_NeverGates()
        {
            // claimWindowEndsAt 0 means "no end recorded yet" and must never block a replay on its own.
            Assert.IsTrue(WorldEventRewardDelivery.ShouldReplay(now: 1_000_000, claimWindowEndsAt: 0, lastPlayedAt: 100, repeatSeconds: 30));
        }

        /// <summary>
        /// The load-bearing case (WP-22 control run): once `now` has reached the claim window's end, no
        /// further replay happens, whatever the cadence says.
        /// </summary>
        [TestMethod]
        public void ShouldReplay_WindowClosed_NeverReplays()
        {
            Assert.IsFalse(WorldEventRewardDelivery.ShouldReplay(now: 2000, claimWindowEndsAt: 2000, lastPlayedAt: 100, repeatSeconds: 30));
            Assert.IsFalse(WorldEventRewardDelivery.ShouldReplay(now: 2500, claimWindowEndsAt: 2000, lastPlayedAt: 100, repeatSeconds: 30));
        }

        // ---- cache effect: PhysicsScriptTable entry -> raw DataID ---------------------------------------

        /// <summary>
        /// The Enchant colors are graded 0 / 0.5 / 1 in the player table; the full-intensity (mod 1) script is
        /// the one sent. Listed out of order so a "first entry" or "last entry" implementation fails.
        /// </summary>
        [TestMethod]
        public void SelectFullIntensityScript_PicksTheHighestMod()
        {
            var did = WorldEventRewardDelivery.SelectFullIntensityScript(new[]
            {
                (0.5f, 0x3300086Bu),
                (1.0f, 0x3300086Cu),
                (0.0f, 0x3300086Au),
            });

            Assert.AreEqual(0x3300086Cu, did);
        }

        /// <summary>WeddingBliss's own shape in 0x34000004: a single entry at mod 1 (0x330006DE).</summary>
        [TestMethod]
        public void SelectFullIntensityScript_SingleEntry_IsReturned()
        {
            Assert.AreEqual(0x330006DEu, WorldEventRewardDelivery.SelectFullIntensityScript(new[] { (1.0f, 0x330006DEu) }));
        }

        [TestMethod]
        public void SelectFullIntensityScript_MissingOrEmpty_IsZero()
        {
            Assert.AreEqual(0u, WorldEventRewardDelivery.SelectFullIntensityScript(null));
            Assert.AreEqual(0u, WorldEventRewardDelivery.SelectFullIntensityScript(Array.Empty<(float, uint)>()));
        }

        [TestMethod]
        public void SelectFullIntensityScript_IgnoresZeroDataIds()
        {
            Assert.AreEqual(0x33000001u, WorldEventRewardDelivery.SelectFullIntensityScript(new[] { (1.0f, 0u), (0.5f, 0x33000001u) }));
        }

        // ---- cache dressing must be unselectable ---------------------------------------------------------

        /// <summary>
        /// Every prop the shipped reward profile places around a cache must carry PropertyBool 24 UiHidden =
        /// True (what UpdateObjectDescriptionFlags turns into ObjectDescriptionFlag.UiHidden 0x80 on the
        /// create packet), and must not carry a PropertyBool 20 row - 20 is SafeSpellComponents, the id the
        /// three dressing units originally set under a "UiHidden" comment, which left the props selectable.
        /// Driven off rewards.json so a new dressing wcid is checked without editing this test.
        /// </summary>
        [TestMethod]
        public void ShippedCacheDressing_EveryPropCarriesUiHidden24()
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

            // Not derived from FindAxesDir: the axes JSON is copied into the test output, the weenie SQL is not,
            // so this walks up to the repo's own Content/sql/weenies.
            var weenieDir = FindRepoDir(Path.Combine("Content", "sql", "weenies"));

            if (weenieDir == null)
                Assert.Inconclusive("Could not locate Content/sql/weenies by walking up from the test assembly -- skipping.");

            var wcids = store.Rewards.Values.SelectMany(r => r.CacheDecor).Select(d => d.Wcid).Distinct().ToArray();

            Assert.IsTrue(wcids.Length > 0, "the shipped profiles place no dressing at all; this test would prove nothing");

            foreach (var wcid in wcids)
            {
                var files = Directory.GetFiles(weenieDir, $"{wcid} *.sql");

                Assert.AreEqual(1, files.Length, $"expected exactly one weenie SQL file for dressing wcid {wcid}");

                // Only the weenie_properties_bool statement: a type-20 or type-24 row in another table (int,
                // float, DID) is a different property and must neither satisfy nor trip this check.
                var boolBlock = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(files[0]),
                    @"INSERT INTO `weenie_properties_bool`[^;]*;");

                Assert.IsTrue(boolBlock.Success, $"dressing wcid {wcid} has no weenie_properties_bool statement");

                var sql = boolBlock.Value;

                StringAssert.Matches(sql, new System.Text.RegularExpressions.Regex($@"\(\s*{wcid}\s*,\s*24\s*,\s*True\s*\)"),
                    $"dressing wcid {wcid} must carry PropertyBool 24 UiHidden = True");

                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\(\s*{wcid}\s*,\s*20\s*,"),
                    $"dressing wcid {wcid} carries a PropertyBool 20 (SafeSpellComponents) row");
            }
        }

        private static string FindRepoDir(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
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
