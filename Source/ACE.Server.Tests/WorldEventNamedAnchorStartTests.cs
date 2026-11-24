using System.Linq;

using ACE.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The in-game "--anchor &lt;id&gt;" start honours a city's maxRadius with the same rule the scheduler uses
    /// (WorldEventAnchorRules). Sources in the composer fixture: element_portal_fire (clearance 10) and ambush (clearance 45).
    /// </summary>
    [TestClass]
    public class WorldEventNamedAnchorStartTests
    {
        private const string Anchors = @"{ ""anchors"": [
            { ""id"": ""tiny"",  ""displayName"": ""Tiny"",  ""cellId"": 27000832, ""originX"": 50, ""originY"": 50, ""originZ"": 0, ""anglesW"": 1, ""maxRadius"": 5 },
            { ""id"": ""mid"",   ""displayName"": ""Mid"",   ""cellId"": 27000832, ""originX"": 50, ""originY"": 50, ""originZ"": 0, ""anglesW"": 1, ""maxRadius"": 10 },
            { ""id"": ""empty"", ""displayName"": ""Empty"", ""cellId"": 27000832, ""originX"": 50, ""originY"": 50, ""originZ"": 0, ""anglesW"": 1, ""maxRadius"": 0 } ] }";

        private static WorldEventRequest Pinned(string anchorId, string sourceId, string goalId)
        {
            return new WorldEventRequest
            {
                SourceId = sourceId,
                FamilyId = "emberwrought",
                GoalId = goalId,
                AnchorId = anchorId,
                Invoker = "test"
            };
        }

        [TestMethod]
        public void ZeroMaxRadiusAnchor_RefusesAPinnedSource()
        {
            var ok = WorldEventComposer.TryCompose(WorldEventInertObjectiveTests.ComposerStore(Anchors), WorldEventInertObjectiveTests.ComposerCatalog(),
                Pinned("empty", "element_portal_fire", "kill_count"), out _, out var error);

            Assert.IsFalse(ok);
            StringAssert.Contains(error, "hosts no World Event");
            StringAssert.Contains(error, "maxRadius is 0");
        }

        [TestMethod]
        public void PinnedSourceBiggerThanTheAnchorsMaxRadius_IsRefusedWithAReason()
        {
            var ok = WorldEventComposer.TryCompose(WorldEventInertObjectiveTests.ComposerStore(Anchors), WorldEventInertObjectiveTests.ComposerCatalog(),
                Pinned("tiny", "element_portal_fire", "kill_count"), out _, out var error);

            Assert.IsFalse(ok);
            StringAssert.Contains(error, "element_portal_fire");
            StringAssert.Contains(error, "only hosts up to 5");
        }

        [TestMethod]
        public void PinnedSourceWithinTheAnchorsMaxRadius_Composes()
        {
            var ok = WorldEventComposer.TryCompose(WorldEventInertObjectiveTests.ComposerStore(Anchors), WorldEventInertObjectiveTests.ComposerCatalog(),
                Pinned("mid", "element_portal_fire", "kill_count"), out var composition, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual("element_portal_fire", composition.Source.Id);
        }

        [TestMethod]
        public void PositionAnchor_IsNotSubjectToAMaxRadius()
        {
            var request = Pinned(null, "ambush", "kill_count");
            request.AnchorPosition = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0);
            request.AnchorLabel = "here";

            var ok = WorldEventComposer.TryCompose(WorldEventInertObjectiveTests.ComposerStore(Anchors), WorldEventInertObjectiveTests.ComposerCatalog(),
                request, out _, out var error);

            Assert.IsTrue(ok, error);
        }

        [TestMethod]
        public void RandomStartAtAnAnchor_OnlyEverRollsSourcesItCanHost()
        {
            var store = WorldEventInertObjectiveTests.ComposerStore(Anchors);
            var catalog = WorldEventInertObjectiveTests.ComposerCatalog();

            for (var seed = 0; seed < 25; seed++)
            {
                var request = new WorldEventRequest { Random = true, AnchorId = "mid", Invoker = "test" };

                var ok = WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new System.Random(seed));

                Assert.IsTrue(ok, error);
                Assert.AreEqual("element_portal_fire", composition.Source.Id, "ambush (clearance 45) does not fit a 10 m city");
            }
        }

        [TestMethod]
        public void RandomStartAtAnAnchorThatHostsNothing_Fails()
        {
            var request = new WorldEventRequest { Random = true, AnchorId = "empty", Invoker = "test" };

            Assert.IsFalse(WorldEventComposer.TryCompose(WorldEventInertObjectiveTests.ComposerStore(Anchors), WorldEventInertObjectiveTests.ComposerCatalog(),
                request, out _, out _, new System.Random(1)));
        }

        // ---- the shared rule itself --------------------------------------------------------------------------

        [TestMethod]
        public void Hosts_RequiresPositiveMaxRadiusAndClearanceWithinIt()
        {
            var source = new SourceThemeDef { GeometryRadius = 10f };

            Assert.IsTrue(WorldEventAnchorRules.Hosts(new AnchorDef { MaxRadius = 10 }, source));
            Assert.IsFalse(WorldEventAnchorRules.Hosts(new AnchorDef { MaxRadius = 9.9f }, source));
            Assert.IsFalse(WorldEventAnchorRules.Hosts(new AnchorDef { MaxRadius = 0 }, new SourceThemeDef { GeometryRadius = 0f }));
            Assert.IsNull(WorldEventAnchorRules.Refusal(new AnchorDef { Id = "a", MaxRadius = 10 }, source));
        }

        [TestMethod]
        public void NarrowSources_ZeroRadius_IsEmpty()
        {
            Assert.AreEqual(0, WorldEventAnchorRules.NarrowSources(new[] { "x" }, 0f, _ => 0f).Count());
        }
    }
}
