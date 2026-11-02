using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// HAZARDS-PLAN S0 world event contract: the element table (wcids, damage types, verbs), TryParse, the request
    /// field and the sources.json hazard block. Pure data.
    /// </summary>
    [TestClass]
    public class WorldEventHazardElementsTests
    {
        [TestMethod]
        public void For_EncodesThePreAssignedWcids_DamageTypes_AndVerbs()
        {
            var expect = new (WorldEventHazardElement E, uint Wisp, uint Ball, DamageType Dmg, string Verb)[]
            {
                (WorldEventHazardElement.Fire, 1006965, 1006966, DamageType.Fire, "sears"),
                (WorldEventHazardElement.Acid, 1006961, 1006957, DamageType.Acid, "corrodes"),
                (WorldEventHazardElement.Frost, 1006962, 1006958, DamageType.Cold, "freezes"),
                (WorldEventHazardElement.Lightning, 1006963, 1006959, DamageType.Electric, "jolts"),
                (WorldEventHazardElement.Earth, 1006964, 1006960, DamageType.Bludgeon, "crushes"),
            };

            foreach (var x in expect)
            {
                var v = WorldEventHazardElements.For(x.E);
                Assert.AreEqual(x.Wisp, v.WispWcid, x.E.ToString());
                Assert.AreEqual(x.Ball, v.BallWcid, x.E.ToString());
                Assert.AreEqual(x.Dmg, v.DamageType, x.E.ToString());
                Assert.AreEqual(x.Verb, v.HitVerb, x.E.ToString());
            }
        }

        [TestMethod]
        public void For_CoversEveryElement_WithTenDistinctWcidsInTheHazardBlock()
        {
            var wcids = new List<uint>();

            foreach (var e in Enum.GetValues(typeof(WorldEventHazardElement)).Cast<WorldEventHazardElement>())
            {
                var v = WorldEventHazardElements.For(e);
                Assert.IsNotNull(v);
                wcids.Add(v.WispWcid);
                wcids.Add(v.BallWcid);
                Assert.AreNotEqual(0u, v.PillarScript);
                Assert.AreNotEqual(0u, v.BurstScript);
                Assert.AreNotEqual(0u, v.MarkerScript);
            }

            Assert.AreEqual(10, wcids.Distinct().Count());
            Assert.IsTrue(wcids.All(w => w >= 1006950 && w <= 1006999));
            CollectionAssert.DoesNotContain(wcids, 1006950u, "Pinnacle's wisp is untouched");
            CollectionAssert.DoesNotContain(wcids, 1006951u, "Pinnacle's ball is untouched");
        }

        [TestMethod]
        public void For_RefusesAnUndefinedElement()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WorldEventHazardElements.For((WorldEventHazardElement)99));
        }

        [TestMethod]
        public void TryParse_AcceptsTheFiveNames_CaseInsensitively()
        {
            foreach (var (text, expected) in new[]
            {
                ("fire", WorldEventHazardElement.Fire), ("ACID", WorldEventHazardElement.Acid), (" Frost ", WorldEventHazardElement.Frost),
                ("lightning", WorldEventHazardElement.Lightning), ("Earth", WorldEventHazardElement.Earth),
            })
            {
                Assert.IsTrue(WorldEventHazardElements.TryParse(text, out var e), text);
                Assert.AreEqual(expected, e, text);
            }
        }

        [TestMethod]
        public void TryParse_RefusesEverythingElse()
        {
            foreach (var text in new[] { null, "", " ", "cold", "electric", "0", "1", "water", "random" })
                Assert.IsFalse(WorldEventHazardElements.TryParse(text, out _), text ?? "null");
        }

        [TestMethod]
        public void Request_Hazard_DefaultsToNull()
        {
            var request = new WorldEventRequest();

            Assert.IsNull(request.Hazard);
            request.Hazard = "random";
            Assert.AreEqual("random", request.Hazard);
        }

        [TestMethod]
        public void SourceThemeDef_Hazard_IsNullWhenAbsent_AndReadsTheBlockWhenPresent()
        {
            var absent = JsonSerializer.Deserialize<SourceThemeDef>("{\"id\":\"x\"}");
            Assert.IsNull(absent.Hazard);

            var json = "{\"id\":\"sky_rift\",\"hazard\":{\"kind\":\"balls\",\"element\":\"fire\","
                + "\"styleElements\":[\"frost\",\"acid\"],\"rainHeight\":42.5}}";
            var present = JsonSerializer.Deserialize<SourceThemeDef>(json);

            Assert.AreEqual("balls", present.Hazard.Kind);
            Assert.AreEqual("fire", present.Hazard.Element);
            CollectionAssert.AreEqual(new[] { "frost", "acid" }, present.Hazard.StyleElements);
            Assert.AreEqual(42.5f, present.Hazard.RainHeight);

            var bare = JsonSerializer.Deserialize<SourceThemeDef>("{\"id\":\"x\",\"hazard\":{\"kind\":\"wisps\"}}");
            Assert.AreEqual("wisps", bare.Hazard.Kind);
            Assert.IsNull(bare.Hazard.Element);
            Assert.IsNull(bare.Hazard.StyleElements);
            Assert.IsNull(bare.Hazard.RainHeight);
        }
    }
}
