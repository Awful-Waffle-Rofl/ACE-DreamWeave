using System;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The optional "hazard" block of a source in sources.json: parsing, normalising and dropping (WorldEventAxisStore.ValidateHazard).</summary>
    [TestClass]
    public class WorldEventAxisStoreHazardTests
    {
        private static string Source(string hazardJson, string extra = "") => @"{ ""sources"": [ {
            ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""disc"",
            ""geometryRadius"": 25.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
            ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
            ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []" + extra +
            (hazardJson == null ? "" : @", ""hazard"": " + hazardJson) + @"
        } ] }";

        private const string TwoStyleDecor = @",
            ""decor"": [
              { ""wcid"": 1002643, ""scale"": 100.0, ""speed"": 0.8, ""dz"": 67.7, ""inverted"": true },
              { ""wcid"": 1002642, ""scale"": 80.0, ""speed"": 0.9, ""dz"": 58.3, ""inverted"": true }
            ],
            ""decorStyles"": [ [1002643, 1002642], [1002645, 1002644], [1002647] ]";

        private static readonly string TwoValidStyles = TwoStyleDecor.Replace(", [1002647] ]", " ]");

        private static WorldEventAxisStore Load(string sources) => WorldEventAxisStore.Parse(sources, null, null, null, null);

        private static int HazardDiagnostics(WorldEventAxisStore store) => store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("hazard"));

        [TestMethod]
        public void NoBlock_IsNull_NoDiagnostic()
        {
            var store = Load(Source(null));

            Assert.IsNull(store.Sources["plain"].Hazard);
            Assert.AreEqual(0, HazardDiagnostics(store));
        }

        [TestMethod]
        public void ValidBlock_IsKept_AndNormalisedToLowerCase()
        {
            var store = Load(Source(@"{ ""kind"": ""WISPS"", ""element"": "" Fire "", ""rainHeight"": 25.5 }"));
            var h = store.Sources["plain"].Hazard;

            Assert.IsNotNull(h);
            Assert.AreEqual("wisps", h.Kind);
            Assert.AreEqual("fire", h.Element);
            Assert.AreEqual(25.5f, h.RainHeight.Value, 1e-4);
            Assert.AreEqual(0, HazardDiagnostics(store));
        }

        [DataTestMethod]
        [DataRow("wisps")]
        [DataRow("balls")]
        [DataRow("rain")]
        [DataRow("random")]
        [DataRow("none")]
        public void EveryKindIsAccepted(string kind)
        {
            Assert.AreEqual(kind, Load(Source(@"{ ""kind"": """ + kind + @""" }")).Sources["plain"].Hazard.Kind);
        }

        [DataTestMethod]
        [DataRow(@"{ ""kind"": ""meteors"" }")]
        [DataRow(@"{ }")]
        [DataRow(@"{ ""kind"": """" }")]
        [DataRow(@"{ ""kind"": ""wisps"", ""element"": ""plasma"" }")]
        [DataRow(@"{ ""kind"": ""rain"", ""rainHeight"": 0 }")]
        [DataRow(@"{ ""kind"": ""rain"", ""rainHeight"": -4 }")]
        [DataRow(@"{ ""kind"": ""rain"", ""rainHeight"": 5000 }")]
        public void BadBlock_IsDropped_ThemeKept_OneDiagnostic(string hazard)
        {
            var store = Load(Source(hazard));

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a bad hazard never costs the theme");
            Assert.IsNull(store.Sources["plain"].Hazard);
            Assert.AreEqual(1, HazardDiagnostics(store));
        }

        [TestMethod]
        public void StyleElements_AlignedWithDecorStyles_AreKept()
        {
            var store = Load(Source(@"{ ""kind"": ""balls"", ""styleElements"": [""Frost"", ""acid""] }", TwoValidStyles));

            CollectionAssert.AreEqual(new[] { "frost", "acid" }, store.Sources["plain"].Hazard.StyleElements.ToArray());
            Assert.AreEqual(0, HazardDiagnostics(store));
        }

        [TestMethod]
        public void StyleElements_WrongCount_AreDropped_KindKept()
        {
            var store = Load(Source(@"{ ""kind"": ""balls"", ""styleElements"": [""frost"", ""acid"", ""fire"", ""earth""] }", TwoValidStyles));
            var h = store.Sources["plain"].Hazard;

            Assert.IsNotNull(h, "only the styleElements go");
            Assert.AreEqual("balls", h.Kind);
            Assert.IsNull(h.StyleElements);
            Assert.AreEqual(1, HazardDiagnostics(store));
        }

        [TestMethod]
        public void StyleElements_UnknownElement_AreDropped()
        {
            var store = Load(Source(@"{ ""kind"": ""balls"", ""styleElements"": [""frost"", ""acid"", ""plasma""] }", TwoStyleDecor));

            Assert.IsNull(store.Sources["plain"].Hazard.StyleElements);
            Assert.AreEqual(1, HazardDiagnostics(store));
        }

        [TestMethod]
        public void StyleElements_MisalignedByADroppedDecorStyle_AreDropped()
        {
            // The third decor style has one wcid where the decor has two colours, so it is dropped: three styleElements
            // no longer line up with the two styles that survive.
            var store = Load(Source(@"{ ""kind"": ""balls"", ""styleElements"": [""frost"", ""acid"", ""fire""] }", TwoStyleDecor));

            Assert.AreEqual(2, store.Sources["plain"].DecorStyles.Count, "fixture: the one-colour style was dropped");
            Assert.IsNull(store.Sources["plain"].Hazard.StyleElements);
            Assert.AreEqual(1, HazardDiagnostics(store));
        }

        // ---- the shipped file -----------------------------------------------------------------------------

        private static string ShippedSources()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes", "sources.json");

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                dir = dir.Parent;
            }

            Assert.Inconclusive("Could not locate Content/events/axes/sources.json by walking up from the test assembly.");
            return null;
        }

        [TestMethod]
        public void ShippedSources_CarryTheHazardBlocks_TheDesignAsks()
        {
            var store = Load(ShippedSources());

            foreach (var element in new[] { "fire", "acid", "frost", "lightning", "earth" })
            {
                var h = store.Sources["element_portal_" + element].Hazard;

                Assert.AreEqual("wisps", h.Kind, element);
                Assert.AreEqual(element, h.Element, element);
            }

            var rift = store.Sources["sky_rift"];

            Assert.AreEqual("balls", rift.Hazard.Kind);
            CollectionAssert.AreEqual(new[] { "frost", "acid", "fire", "lightning" }, rift.Hazard.StyleElements.ToArray());
            Assert.AreEqual(rift.DecorStyles.Count, rift.Hazard.StyleElements.Count, "index-aligned with decorStyles");
            Assert.AreEqual("balls", store.Sources["weave_spiral"].Hazard.Kind);
            Assert.IsNull(store.Sources["rift"].Hazard);
            Assert.IsNull(store.Sources["ambush"].Hazard);
            Assert.AreEqual(0, store.Diagnostics.Count(d => d.Contains("hazard")), "no shipped hazard block is dropped");
        }

        [TestMethod]
        public void ShippedSkyRift_StyleOrder_IsBlueGreenRedGrey_ForFrostAcidFireLightning()
        {
            // 1002643 Blue / 1002642 Purple, 1002645 Green / 1002644 Yellow, 1002647 Red / 1002646 Orange,
            // 1002649 Grey / 1002642 Purple (SkyDecorColours): the first colour of each pair names the style.
            var styles = Load(ShippedSources()).Sources["sky_rift"].DecorStyles;

            CollectionAssert.AreEqual(new uint[] { 1002643, 1002645, 1002647, 1002649 }, styles.Select(s => s[0]).ToArray());
        }
    }
}
