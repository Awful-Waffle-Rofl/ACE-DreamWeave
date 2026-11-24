using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The shipped Content/events/axes/anchors.json: the 20 market city anchors the daily scheduler picks from.
    /// </summary>
    [TestClass]
    public class WorldEventAnchorsTests
    {
        // The market's world-event-anchors.json ids (dreamweave-market Source/ACE.Market/Data/world-event-anchors.json).
        private static readonly string[] MarketIds =
        {
            "zaikhal", "silyun", "bluespire", "oolutanga", "eastwatch", "xarabydun", "uziz", "samsur", "lin", "yanshi",
            "yaraq", "redspire", "al_jalima", "westwatch", "sanamar", "stonehold", "rithwic", "eastham", "shoushi", "dryreach"
        };

        private static WorldEventAxisStore ShippedStore()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sources.json")))
                {
                    return WorldEventAxisStore.Parse(
                        File.ReadAllText(Path.Combine(candidate, "sources.json")),
                        File.ReadAllText(Path.Combine(candidate, "families.json")),
                        File.ReadAllText(Path.Combine(candidate, "goals.json")),
                        File.ReadAllText(Path.Combine(candidate, "rewards.json")),
                        File.ReadAllText(Path.Combine(candidate, "anchors.json")));
                }

                dir = dir.Parent;
            }

            Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");
            return null;
        }

        [TestMethod]
        public void ShippedAnchors_AreTheTwentyMarketCities()
        {
            var store = ShippedStore();

            Assert.AreEqual(20, store.Anchors.Count);
            CollectionAssert.AreEquivalent(MarketIds, store.Anchors.Keys.ToList());
            Assert.AreEqual(0, store.Diagnostics.Count(d => d.StartsWith("anchors.json")), "anchors.json loads with no diagnostics");
        }

        [TestMethod]
        public void Dryreach_HostsNothing_EveryOtherCityHostsSomething()
        {
            var store = ShippedStore();

            Assert.AreEqual(0f, store.Anchors["dryreach"].MaxRadius);

            foreach (var anchor in store.Anchors.Values.Where(a => a.Id != "dryreach"))
                Assert.IsTrue(anchor.MaxRadius > 0, $"{anchor.Id} has maxRadius {anchor.MaxRadius}");
        }

        [TestMethod]
        public void KnownAnchorValues_RoundTripFromTheMarketList()
        {
            var store = ShippedStore();

            var silyun = store.Anchors["silyun"];
            Assert.AreEqual("Silyun", silyun.DisplayName);
            Assert.AreEqual(0x27EC0014u, silyun.CellId);
            Assert.AreEqual(51.93f, silyun.OriginX);
            Assert.AreEqual(90.33f, silyun.OriginY);
            Assert.AreEqual(80.0f, silyun.OriginZ);
            Assert.AreEqual(20f, silyun.MaxRadius);
            Assert.AreEqual((ushort)0, silyun.RealmId);

            var zaikhal = store.Anchors["zaikhal"];
            Assert.AreEqual(0x8090000Bu, zaikhal.CellId);
            Assert.AreEqual(0.648841f, zaikhal.AnglesW);
            Assert.AreEqual(-0.760924f, zaikhal.AnglesZ);

            Assert.AreEqual("Oolutanga's Refuge", store.Anchors["oolutanga"].DisplayName);
            Assert.AreEqual("Al-Jalima", store.Anchors["al_jalima"].DisplayName);
        }

        [TestMethod]
        public void EveryHostingCity_FitsAtLeastOneRandomRollableWebSource()
        {
            var store = ShippedStore();

            foreach (var anchor in store.Anchors.Values.Where(a => a.MaxRadius > 0))
            {
                var fitting = WorldEventAdminService.FittingWebSources(store, anchor.OriginX, anchor.OriginY)
                    .Select(id => store.Sources[id])
                    .Where(s => s.RandomWeight > 0
                                && s.ClearanceRadius <= anchor.MaxRadius
                                && WorldEventAdminService.SourceFits(anchor.OriginX, anchor.OriginY, s))
                    .Select(s => s.Id)
                    .ToList();

                Assert.IsTrue(fitting.Count > 0,
                    $"{anchor.Id}: no web-startable, random-rollable source has clearance <= {anchor.MaxRadius} and fits the landblock edge margin at ({anchor.OriginX}, {anchor.OriginY})");
            }
        }

        [TestMethod]
        public void EveryHostingCity_BuildsARandomRequestThroughTheWebPath()
        {
            var store = ShippedStore();

            foreach (var anchor in store.Anchors.Values.Where(a => a.MaxRadius > 0))
            {
                var body = WorldEventScheduler.BodyFor(anchor);

                var refused = WorldEventAdminService.TryBuildRequest(body, store, id => id == 0, 60, "scheduler", out var request, out _);

                Assert.IsNull(refused, $"{anchor.Id}: {refused?.Reason}");
                Assert.IsTrue(request.Random);
                Assert.IsTrue(request.AllowedSourceIds.Count > 0, anchor.Id);

                var narrowed = WorldEventAnchorRules.NarrowSources(request.AllowedSourceIds, anchor.MaxRadius,
                    id => store.Sources.TryGetValue(id, out var s) ? s.ClearanceRadius : null);

                Assert.IsTrue(narrowed.Count > 0, $"{anchor.Id}: no allowed source clears maxRadius {anchor.MaxRadius}");
            }
        }
    }
}
