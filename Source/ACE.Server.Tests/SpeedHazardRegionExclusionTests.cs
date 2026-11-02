using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The optional hazard-region exclusions and hazard origin of speed-courses.json: parsed, validated, passed to
    /// HazardRegion, and absent without changing anything for a course that authors neither.
    /// </summary>
    [TestClass]
    public class SpeedHazardRegionExclusionTests
    {
        private const string SouthRegion = @"""region"": { ""shape"": ""rect"", ""minX"": 190, ""maxX"": 210, ""minY"": -430, ""maxY"": -410, ""groundZ"": -6 }";

        private static string WithSouthRegion(string region)
            => SpeedCourseFixtures.SampleJson.Replace(
                @"""region"": { ""shape"": ""rect"", ""minX"": 0, ""maxX"": 10, ""minY"": -30, ""maxY"": -10, ""groundZ"": -6 }",
                region);

        private static SpeedHazardDef South(SpeedCourseStore store) => store.GetCourse(3).Hazards.Single(h => h.Id == "south_memory");

        [TestMethod]
        public void NoExclusions_IsBackwardCompatible()
        {
            var store = SpeedCourseStore.Parse(SpeedCourseFixtures.SampleJson, null);
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("; ", store.Diagnostics));

            foreach (var h in store.GetCourse(3).Hazards)
            {
                Assert.IsNull(h.Region.Exclusions, $"{h.Id}: absent stays null");
                Assert.AreEqual(0, h.Region.Build(h.Id, 0x0089, 0x80089123u).Exclusions.Count, $"{h.Id}: no exclusions in the built region");
            }
        }

        [TestMethod]
        public void Exclusions_AreParsed_AndPassedToTheRegion()
        {
            var json = WithSouthRegion(SouthRegion.Replace(@"""groundZ"": -6 }",
                @"""groundZ"": -6, ""exclusions"": [ { ""centerX"": 199, ""centerY"": -422, ""radius"": 2.5 } ] }"));

            var store = SpeedCourseStore.Parse(json, null);
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("; ", store.Diagnostics));

            var def = South(store);
            Assert.AreEqual(1, def.Region.Exclusions.Count);

            var region = def.Region.Build("speed:south_memory", 0x0089, 0x80089123u);
            Assert.AreEqual(1, region.Exclusions.Count);

            // The chute is inside the rect but no hazard may be placed on it.
            Assert.IsTrue(region.Contains(199f, -422f));
            Assert.IsTrue(region.InExclusion(199f, -422f));
            Assert.IsFalse(region.IsPlaceable(199f, -422f));
            Assert.IsFalse(region.InExclusion(199f, -418f), "2.5 m radius: 4 m away is clear");
            Assert.IsTrue(region.IsPlaceable(205f, -415f));

            // A player standing on the chute still counts as in the region (exclusions are never a safe spot).
            Assert.IsTrue(def.Region.Contains(199f, -422f, -6f));
        }

        [TestMethod]
        public void BadExclusion_IsADiagnostic_AndDropsTheCourse()
        {
            var json = WithSouthRegion(SouthRegion.Replace(@"""groundZ"": -6 }",
                @"""groundZ"": -6, ""exclusions"": [ { ""centerX"": 199, ""centerY"": -422, ""radius"": 0 } ] }"));

            var store = SpeedCourseStore.Parse(json, null);

            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("south_memory") && d.Contains("exclusion 0")), string.Join("; ", store.Diagnostics));
            Assert.IsNull(store.GetCourse(3));
        }

        [TestMethod]
        public void TriggerRegion_DefaultsToTheRegion_AndIsValidated()
        {
            var course = SpeedCourseFixtures.Course();
            var balls = course.Hazards.Single(h => h.Id == "east_balls");
            var zones = course.Hazards.Single(h => h.Id == "cloister_zones");

            Assert.IsNotNull(balls.TriggerRegion);
            Assert.AreSame(balls.TriggerRegion, balls.TriggerArea);
            Assert.IsTrue(balls.TriggerArea.Contains(65, 25, -6));
            Assert.IsFalse(balls.Region.Contains(65, 25, -6), "the trigger area is wider than where the balls play");
            Assert.IsNull(zones.TriggerRegion);
            Assert.AreSame(zones.Region, zones.TriggerArea, "absent: the region itself");

            var bad = SpeedCourseFixtures.SampleJson.Replace(
                @"""triggerRegion"": { ""shape"": ""rect"", ""minX"": 30, ""maxX"": 70,",
                @"""triggerRegion"": { ""shape"": ""rect"", ""minX"": 30, ""maxX"": 30,");
            var store = SpeedCourseStore.Parse(bad, null);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("east_balls' triggerRegion") && d.Contains("zero width")), string.Join("; ", store.Diagnostics));
        }

        [TestMethod]
        public void Origin_DefaultsToBossForTheIntervalTrigger_AndIsValidated()
        {
            var course = SpeedCourseFixtures.Course();

            Assert.IsTrue(course.Hazards.Single(h => h.Id == "boss_fans").OriginAtBoss, "boss_alive_interval defaults to the boss");
            Assert.IsFalse(course.Hazards.Single(h => h.Id == "hub_zones").OriginAtBoss, "boss_health_below stays region-centred");
            Assert.IsFalse(course.Hazards.Single(h => h.Id == "east_balls").OriginAtBoss);

            var explicitRegion = SpeedCourseFixtures.SampleJson.Replace(@"""seconds"": 20 }, ""args"": {}", @"""seconds"": 20 }, ""args"": {}, ""origin"": ""region""");
            var store = SpeedCourseStore.Parse(explicitRegion, null);
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("; ", store.Diagnostics));
            Assert.IsFalse(store.GetCourse(3).Hazards.Single(h => h.Id == "boss_fans").OriginAtBoss, "an explicit origin wins");

            var bad = SpeedCourseFixtures.SampleJson.Replace(@"""seconds"": 20 }, ""args"": {}", @"""seconds"": 20 }, ""args"": {}, ""origin"": ""head""");
            var badStore = SpeedCourseStore.Parse(bad, null);
            Assert.IsTrue(badStore.Diagnostics.Any(d => d.Contains("origin 'head'")), string.Join("; ", badStore.Diagnostics));
        }
    }
}
