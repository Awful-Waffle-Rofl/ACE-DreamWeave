using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The gauntlet_t0 to gauntlet_t6 rows in rewards.json (Task 12): they parse clean, carry the Gauntlet's cache and crate, and
    /// stay out of World Events. The composer only ever picks its default reward id, so a profile added for the Gauntlet must
    /// never be what an event pays.
    /// </summary>
    [TestClass]
    public class GauntletRewardProfilesTests
    {
        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "bosses.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static WorldEventAxisStore ShippedStore()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            string Read(string name) => File.ReadAllText(Path.Combine(dir, name));

            return WorldEventAxisStore.Parse(Read("sources.json"), Read("families.json"), Read("goals.json"), Read("rewards.json"), Read("anchors.json"), Read("bosses.json"));
        }

        [TestMethod]
        public void GauntletProfiles_ShippedForEveryTier_WithTheGauntletCacheAndCrate()
        {
            var store = ShippedStore();

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray(), "the shipped rewards validate clean");

            for (var tier = 0; tier <= GauntletTuning.CrateMaxTierCeiling; tier++)
            {
                var id = GauntletCrateTier.ProfilePrefix + tier;

                Assert.IsTrue(store.Rewards.TryGetValue(id, out var reward), $"{id} is shipped");
                Assert.AreEqual(GauntletRewards.CrateWcid, reward.SuccessCrateWcid, id);
                Assert.AreEqual(GauntletRewards.CacheWcid, reward.CacheWcid, id);
                Assert.IsTrue(reward.Pool != null && reward.Pool.Count > 0, $"{id} has a pool, or its crate would refuse to open");
                Assert.AreEqual(store.Rewards["standard"].ClaimWindowSeconds, reward.ClaimWindowSeconds, $"{id} copies the standard claim window");
                Assert.AreEqual(store.Rewards["standard"].Pool.Count, reward.Pool.Count, $"{id} copies the standard pool");
                Assert.IsTrue(reward.GateByCharacter && reward.GateByAccount && reward.GateByIp, $"{id} copies the standard gates");
                Assert.AreEqual(0, reward.CacheWcids.Count, $"{id} uses its one cache look");
            }
        }

        [TestMethod]
        public void GauntletProfiles_NotPickedByComposerDefault()
        {
            var store = ShippedStore();

            Assert.AreEqual("standard", WorldEventComposer.DefaultRewardId);

            var catalog = new WorldEventCatalog(new Dictionary<string, IReadOnlyList<FamilyMember>>
            {
                ["tusker"] = new List<FamilyMember> { new FamilyMember { Wcid = 9002, Name = "Tusker", Level = 100, Role = 0 } },
            }, new List<string>());

            WorldEventRequest Request() => new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "tusker",
                GoalId = "kill_count",
                AnchorPosition = new ACE.Entity.Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test",
            };

            // An ordinary request names no reward: it must resolve to standard, never to a Gauntlet profile.
            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, Request(), out var composition, out var error), error);
            Assert.AreEqual("standard", composition.Reward.Id);

            // Many random compositions, none of which may land on a Gauntlet profile.
            for (var seed = 0; seed < 200; seed++)
            {
                var random = Request();
                random.Random = true;

                if (WorldEventComposer.TryCompose(store, catalog, random, out var composed, out _, new Random(seed)))
                    Assert.IsFalse(composed.Reward.Id.StartsWith(GauntletCrateTier.ProfilePrefix, StringComparison.Ordinal), $"seed {seed} composed {composed.Reward.Id}");
            }

            // And with the standard row gone the composer REFUSES a request that names no reward, rather than falling to a Gauntlet row.
            var dir = FindAxesDir();
            string Read(string name) => File.ReadAllText(Path.Combine(dir, name));
            var rewards = System.Text.Json.Nodes.JsonNode.Parse(Read("rewards.json"));
            var rows = rewards["rewards"].AsArray();

            for (var i = rows.Count - 1; i >= 0; i--)
                if ((string)rows[i]["id"] == "standard")
                    rows.RemoveAt(i);

            var noStandard = WorldEventAxisStore.Parse(Read("sources.json"), Read("families.json"), Read("goals.json"), rewards.ToJsonString(), Read("anchors.json"), Read("bosses.json"));
            Assert.AreEqual(7, noStandard.Rewards.Count);

            Assert.IsFalse(WorldEventComposer.TryCompose(noStandard, catalog, Request(), out _, out var refusal));
            StringAssert.Contains(refusal, "reward");
        }
    }
}
