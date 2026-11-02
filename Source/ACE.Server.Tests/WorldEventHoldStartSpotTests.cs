using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldEvents.Objectives;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H5: goals.json townReach (parse and validation), the town reach rules, and the Hold start-spot rule
    /// over a fake landscape (no dat, no PropertyManager, no landblock). The real defend_envoys goal is loaded from
    /// Content/events/axes so the rule runs against the shipped clearArea and portals.
    /// </summary>
    [TestClass]
    public class WorldEventHoldStartSpotTests
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

        private static GoalDef Envoys()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());
            Assert.IsTrue(store.Goals.TryGetValue(GoalId, out var goal), "defend_envoys did not load");
            return goal;
        }

        /// <summary>Open, flat ground everywhere (or where <see cref="Ok"/> says), no obstacles, height 50.</summary>
        private sealed class FakeLand : IWorldEventLandscape
        {
            public Func<double, double, bool> Ok = (x, y) => true;
            public List<AreaObstacle> Obstacles = new List<AreaObstacle>();

            public IReadOnlyList<AreaObstacle> ObstaclesNear(double globalX, double globalY, double radius, double ignoreRadius) => Obstacles;
            public bool GroundOk(double globalX, double globalY) => Ok(globalX, globalY);
            public float? TerrainZ(double globalX, double globalY) => 50f;
        }

        // An outdoor anchor: landblock 0xA9B4, cell 0x0019, local (84, 7).
        private static Position Anchor() => new Position(0xA9B40019, 84f, 7f, 10f, 0f, 0f, 0f, 1f, 0);

        private static WorldEventComposition Compose(GoalDef goal, Position anchor)
        {
            var source = new SourceThemeDef { Id = "ambush", DisplayName = "Ambush", CompatibleGoals = new List<string> { goal.Id } };
            var family = new FamilyDef { Id = "f", DisplayName = "the F", BiomeTags = new List<string>(), Members = new List<FamilyMember>() };
            var reward = new RewardDef { Id = "standard", DisplayName = "Crate" };
            var anchorDef = new AnchorDef { Id = "here", DisplayName = "here", CellId = anchor.LandblockId.Raw, BiomeTags = new List<string>() };

            return new WorldEventComposition(source, new[] { family }, BossDef.None, goal, reward, anchorDef, anchor, WorldEventAxisStore.Empty);
        }

        private static double GX(Position p) => p.LandblockId.LandblockX * 192.0 + p.PositionX;
        private static double GY(Position p) => p.LandblockId.LandblockY * 192.0 + p.PositionY;

        // ---- townReach ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void DefendEnvoys_TownReach_ParsesWithTheShippedValues()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());
            var mine = store.Diagnostics.Where(d => d.Contains(GoalId, StringComparison.Ordinal)).ToList();

            Assert.AreEqual(0, mine.Count, "defend_envoys raised load diagnostics: " + string.Join(" | ", mine));
            Assert.IsTrue(store.Goals.TryGetValue(GoalId, out var goal));
            Assert.IsNotNull(goal.TownReach, "townReach did not bind");
            Assert.AreEqual(250.0, goal.TownReach.MaxWalk);
            Assert.AreEqual(60.0, goal.TownReach.SpotMinDistance);
            Assert.AreEqual(160.0, goal.TownReach.SpotMaxDistance);
            Assert.AreEqual(4, goal.TownReach.Towns);
            Assert.IsNull(WorldEventTownReach.Validate(goal.TownReach));
        }

        [TestMethod]
        public void TownReach_Validate_RefusesEachOutOfRangeField_AndAcceptsAbsent()
        {
            Assert.IsNull(WorldEventTownReach.Validate(null));
            Assert.IsNull(WorldEventTownReach.Validate(new TownReachDef()));

            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { MaxWalk = 10 }));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { SpotMinDistance = 5 }));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { SpotMaxDistance = 400 }), "above maxWalk");
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { SpotMinDistance = 100, SpotMaxDistance = 100 }));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { Towns = 0 }));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { Towns = 11 }));
        }

        [TestMethod]
        public void TownReach_ReachableOnlyWithinMaxWalk_AndRerollTakesTheNearestTowns()
        {
            var towns = new List<WorldEventTown>
            {
                new WorldEventTown("Far", 10000f, 10000f),
                new WorldEventTown("Near", 1000f, 1000f),
                new WorldEventTown("Mid", 3000f, 3000f),
            };

            Assert.IsTrue(WorldEventTownReach.IsReachable(1100, 1000, towns, 250));
            Assert.IsFalse(WorldEventTownReach.IsReachable(1300, 1000, towns, 250));
            Assert.IsFalse(WorldEventTownReach.IsReachable(1000, 1000, new List<WorldEventTown>(), 250), "no towns: nothing is reachable");

            var picked = WorldEventTownReach.RerollTowns(towns, 0, 0, new TownReachDef { Towns = 2 });
            CollectionAssert.AreEqual(new[] { "Near", "Mid" }, picked.Select(t => t.Name).ToArray());
        }

        [TestMethod]
        public void TownReach_SpotsNear_AllLieBetweenMinAndMaxDistance()
        {
            var town = new WorldEventTown("T", 5000f, 7000f);
            var reach = new TownReachDef { MaxWalk = 250, SpotMinDistance = 60, SpotMaxDistance = 160, Towns = 4 };

            var spots = WorldEventTownReach.SpotsNear(town, reach, new Random(3));

            Assert.AreEqual(6 * 12, spots.Count, "rings at 60, 80, ..., 160, twelve bearings each");

            foreach (var (x, y) in spots)
            {
                var d = Math.Sqrt((x - 5000) * (x - 5000) + (y - 7000) * (y - 7000));
                Assert.IsTrue(d >= 60 - 1e-6 && d <= 160 + 1e-6, $"spot at {d:0.0} m");
            }
        }

        [TestMethod]
        public void AnchorAt_MovesAnOutdoorAnchor_ToTheGlobalPoint_WithTheEnginesCellSplit()
        {
            var anchor = Anchor();
            var gx = 0x12 * 192.0 + 100.5;
            var gy = 0x34 * 192.0 + 30.25;

            Assert.IsTrue(WorldEventHoldStartSpotRule.AnchorAt(anchor, gx, gy, 42f));

            Assert.AreEqual(0x12u, (anchor.LandblockId.Raw >> 24) & 0xFF);
            Assert.AreEqual(0x34u, (anchor.LandblockId.Raw >> 16) & 0xFF);
            Assert.AreEqual(100.5f, anchor.PositionX, 1e-3);
            Assert.AreEqual(30.25f, anchor.PositionY, 1e-3);
            Assert.AreEqual(42f, anchor.PositionZ);
            Assert.AreEqual((uint)(4 * 8 + 1 + 1), anchor.LandblockId.Raw & 0xFFFF, "cell x 4 (100.5 / 24), y 1 (30.25 / 24)");
            Assert.AreEqual(gx, anchor.ToGlobal().X, 1e-2, "round-trips through the engine's own ToGlobal");

            var indoor = new Position(0x01D90108, 1f, 1f, 1f, 0f, 0f, 0f, 1f, 0);
            Assert.IsFalse(WorldEventHoldStartSpotRule.AnchorAt(indoor, gx, gy, 0f), "an indoor anchor is never moved");
            Assert.IsFalse(WorldEventHoldStartSpotRule.AnchorAt(Anchor(), -5, gy, 0f), "outside the grid");
        }

        // ---- the start-spot rule -------------------------------------------------------------------------------------

        [TestMethod]
        public void Rule_IsTheHoldRule_ForHoldGoals()
        {
            Assert.AreSame(WorldEventHoldStartSpotRule.Instance, WorldEventObjectiveFactory.StartSpotRuleFor(Envoys()));
        }

        /// <summary>
        /// The rule with the settings resolver pinned to <paramref name="settings"/> (full property names to values): no
        /// PropertyManager is read.
        /// </summary>
        private static WorldEventHoldStartSpotRule Rule(FakeLand land, IReadOnlyList<WorldEventTown> towns, int seed,
            Dictionary<string, string> settings = null)
        {
            return new WorldEventHoldStartSpotRule(() => land, () => towns, new Random(seed),
                g => WorldEventHoldSettingsResolver.Resolve(g, k => settings != null && settings.TryGetValue(k, out var v) ? v : null));
        }

        // Every real start names a spot (an admin's --here, or the scheduler's picked anchor), so every request below pins it.
        private static WorldEventRequest Asked(Position anchor) => new WorldEventRequest { AnchorPosition = new Position(anchor) };

        [TestMethod]
        public void Rule_OpenGroundNearATown_KeepsTheAnchor_AndLeavesThePortalPlan()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var town = new WorldEventTown("Here", (float)GX(anchor) + 100f, (float)GY(anchor));
            var before = anchor.LandblockId.Raw;

            Assert.IsTrue(Rule(new FakeLand(), new[] { town }, 1).TryResolve(composition, Asked(anchor), out var error), error);
            Assert.IsNull(error);
            Assert.AreEqual(before, anchor.LandblockId.Raw);
            Assert.AreEqual(84f, anchor.PositionX);
            Assert.IsNotNull(composition.AreaPlan, "the clear-area plan is handed on");
            Assert.IsTrue(composition.AreaPlan.Ok, composition.AreaPlan.Why);
            Assert.AreEqual(goal.Protect.Portals.Count, composition.AreaPlan.Portals.Count, "open ground: every portal stands");
        }

        /// <summary>
        /// Review finding F2: every real start sets AnchorPosition, so a rule that skipped the re-roll for a pinned anchor
        /// never re-rolled at all. An asked spot out of reach of every town re-rolls near the nearest town, pinned or not.
        /// </summary>
        [TestMethod]
        public void Rule_AnAskedSpotTooFarFromTown_ReRollsNearTheNearestTown_EvenWhenPinned()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var town = new WorldEventTown("Holtburg", (float)GX(anchor) + 3000f, (float)GY(anchor) + 2000f);

            Assert.IsTrue(Rule(new FakeLand(), new[] { town }, 7).TryResolve(composition, Asked(anchor), out var error), error);

            var d = Math.Sqrt(Math.Pow(GX(anchor) - town.GlobalX, 2) + Math.Pow(GY(anchor) - town.GlobalY, 2));

            Assert.IsTrue(d >= 60 - 1 && d <= 160 + 1, $"moved to {d:0} m from the town (spotMin 60, spotMax 160)");
            Assert.AreEqual(50f, anchor.PositionZ, "the landscape's terrain height");
            Assert.IsFalse(anchor.Indoors);
            Assert.IsNotNull(composition.AreaPlan);
        }

        /// <summary>
        /// F2: the asked spot is in reach of a town but its ground is closed (no clear area within searchRadius): the start
        /// re-rolls to open ground near the town instead of standing on the blocked spot.
        /// </summary>
        [TestMethod]
        public void Rule_AnAskedSpotWithNoClearAreaHere_ReRollsNearTheTown()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var ax = GX(anchor);
            var ay = GY(anchor);
            var town = new WorldEventTown("Near", (float)ax + 200f, (float)ay);

            // Closed within 120 m of the asked spot (wider than the search radius and the ring), open beyond it.
            var land = new FakeLand { Ok = (x, y) => Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay)) > 120 };

            Assert.IsTrue(Rule(land, new[] { town }, 5).TryResolve(composition, Asked(anchor), out var error), error);

            var fromAsked = Math.Sqrt(Math.Pow(GX(anchor) - ax, 2) + Math.Pow(GY(anchor) - ay, 2));
            var fromTown = Math.Sqrt(Math.Pow(GX(anchor) - town.GlobalX, 2) + Math.Pow(GY(anchor) - town.GlobalY, 2));

            Assert.IsTrue(fromAsked > 120, $"moved off the closed ground ({fromAsked:0} m from the asked spot)");
            Assert.IsTrue(fromTown >= 60 - 1 && fromTown <= 160 + 1, $"to a spot near the town ({fromTown:0} m)");
            Assert.IsNotNull(composition.AreaPlan);
            Assert.IsTrue(composition.AreaPlan.Ok, composition.AreaPlan.Why);
        }

        /// <summary>
        /// Review finding N1: the announcements and the admin label read Composition.Anchor.DisplayName. A re-rolled start
        /// is named where it now stands (the nearest town), through a copy; the composed AnchorDef is never written.
        /// </summary>
        [TestMethod]
        public void Rule_AReRolledStart_IsNamedAfterTheNearestTown_ThroughACopy()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var asked = composition.Anchor;
            var town = new WorldEventTown("Holtburg", (float)GX(anchor) + 3000f, (float)GY(anchor) + 2000f);

            Assert.AreEqual("here", asked.DisplayName, "control: the request's label");

            Assert.IsTrue(Rule(new FakeLand(), new[] { town }, 7).TryResolve(composition, Asked(anchor), out var error), error);

            Assert.AreNotSame(asked, composition.Anchor, "replaced by a copy");
            Assert.AreEqual("here", asked.DisplayName, "the composed (shared) AnchorDef is untouched");
            Assert.AreNotEqual(asked.DisplayName, composition.Anchor.DisplayName);
            StringAssert.StartsWith(composition.Anchor.DisplayName, "Holtburg (", "names the nearest town");
            Assert.AreEqual(WorldEventTownIndex.Describe(anchor, new[] { town }), composition.Anchor.DisplayName);
            Assert.AreEqual(asked.Id, composition.Anchor.Id, "the id is kept");
        }

        [TestMethod]
        public void Rule_AnUnmovedStart_KeepsItsLabel_AndItsAnchorDef()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var asked = composition.Anchor;
            var town = new WorldEventTown("Here", (float)GX(anchor) + 100f, (float)GY(anchor));

            Assert.IsTrue(Rule(new FakeLand(), new[] { town }, 1).TryResolve(composition, Asked(anchor), out var error), error);

            Assert.IsNotNull(composition.AreaPlan, "control: the rule ran Apply");
            Assert.AreSame(asked, composition.Anchor);
            Assert.AreEqual("here", composition.Anchor.DisplayName);
        }

        [TestMethod]
        public void Renamed_CopiesEveryField_AndTheBiomeList()
        {
            var a = new AnchorDef { Id = "x", DisplayName = "X", CellId = 7, OriginX = 1, OriginY = 2, OriginZ = 3, AnglesW = 1, AnglesZ = 0.5f, BiomeTags = new List<string> { "plains" }, CooldownSeconds = 60 };

            var b = WorldEventHoldStartSpotRule.Renamed(a, "Y");

            Assert.AreEqual("Y", b.DisplayName);
            Assert.AreEqual("X", a.DisplayName);
            // Every property, so a field added to AnchorDef later fails here until Renamed copies it.
            foreach (var prop in typeof(AnchorDef).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name != nameof(AnchorDef.DisplayName)))
            {
                if (prop.PropertyType == typeof(List<string>))
                    continue;

                var fresh = Activator.CreateInstance(typeof(AnchorDef));
                var probe = (AnchorDef)fresh;
                object marker = prop.PropertyType == typeof(string) ? "m" : Convert.ChangeType(9, prop.PropertyType);
                prop.SetValue(probe, marker);

                Assert.AreEqual(marker, prop.GetValue(WorldEventHoldStartSpotRule.Renamed(probe, "Y")), $"AnchorDef.{prop.Name} is not copied");
            }

            Assert.AreNotSame(a.BiomeTags, b.BiomeTags);
            CollectionAssert.AreEqual(a.BiomeTags, b.BiomeTags);
            Assert.IsNull(WorldEventHoldStartSpotRule.Renamed(null, "Y"));
        }

        /// <summary>F2: nothing open anywhere: the start is NOT refused; it stands at the asked spot (with a warning).</summary>
        [TestMethod]
        public void Rule_NoOpenGroundAnywhere_StandsAtTheAskedSpot_NeverRefuses()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var town = new WorldEventTown("Far", (float)GX(anchor) + 3000f, (float)GY(anchor));
            var before = anchor.LandblockId.Raw;

            Assert.IsTrue(Rule(new FakeLand { Ok = (x, y) => false }, new[] { town }, 1).TryResolve(composition, Asked(anchor), out var error));
            Assert.IsNull(error);
            Assert.AreEqual(before, anchor.LandblockId.Raw, "unchanged");
            Assert.AreEqual(84f, anchor.PositionX);
            Assert.IsNull(composition.AreaPlan, "no plan: the objective falls back to the configured ring");
        }

        /// <summary>
        /// Review finding F4: portal_count / portal_radius were dead whenever a clear-area plan existed, because the plan was
        /// built from protect.portals alone. They are now resolved before the search.
        /// </summary>
        [TestMethod]
        public void Rule_PortalCountAndRadiusSettings_ShapeThePlan()
        {
            var goal = Envoys();
            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var town = new WorldEventTown("Here", (float)GX(anchor) + 100f, (float)GY(anchor));
            var settings = new Dictionary<string, string>
            {
                [HoldKeys.PropertyPrefix + HoldKeys.PortalCount] = "2",
                [HoldKeys.PropertyPrefix + HoldKeys.PortalRadius] = "22",
            };

            Assert.AreNotEqual(2, goal.Protect.Portals.Count, "control: the shipped count differs from the setting");

            Assert.IsTrue(Rule(new FakeLand(), new[] { town }, 1, settings).TryResolve(composition, Asked(anchor), out var error), error);
            Assert.IsNotNull(composition.AreaPlan);
            Assert.AreEqual(2, composition.AreaPlan.Portals.Count, "portal_count reached the plan");
            Assert.AreEqual(4, goal.Protect.Portals.Count, "the goal's own def is never changed");
        }

        [TestMethod]
        public void EffectivePortals_AppliesTheSettings_OverACopy()
        {
            var def = new PortalsDef { Count = 4, Radius = 30, RotationDegrees = 45, Scale = 0.9, Inset = 2, PulseEffect = "PortalExit" };
            var tuning = new WorldEventHoldSpawnerTuning { PortalCount = 3, PortalRadius = 18 };

            var eff = WorldEventHoldStartSpotRule.EffectivePortals(def, tuning);

            Assert.AreNotSame(def, eff);
            Assert.AreEqual(3, eff.Count);
            Assert.AreEqual(18.0, eff.Radius);
            Assert.AreEqual(45.0, eff.RotationDegrees);
            Assert.AreEqual(0.9, eff.Scale);
            Assert.AreEqual(2.0, eff.Inset);
            Assert.AreEqual("PortalExit", eff.PulseEffect);
            Assert.AreEqual(4, def.Count, "the def is untouched");

            var none = WorldEventHoldStartSpotRule.EffectivePortals(def, WorldEventHoldSpawnerTuning.Defaults);
            Assert.AreEqual(4, none.Count, "unset settings keep protect.portals");
            Assert.AreEqual(30.0, none.Radius);
            Assert.IsNull(WorldEventHoldStartSpotRule.EffectivePortals(null, tuning));
        }

        [TestMethod]
        public void Rule_NoClearAreaAndNoTownReach_LeavesEverythingAlone_WithoutReadingTheLandscape()
        {
            var goal = Envoys();
            goal.TownReach = null;
            goal.Protect.ClearArea = null;

            var anchor = Anchor();
            var composition = Compose(goal, anchor);
            var read = false;

            var rule = new WorldEventHoldStartSpotRule(() => { read = true; return new FakeLand(); }, () => Array.Empty<WorldEventTown>(), new Random(1));

            Assert.IsTrue(rule.TryResolve(composition, new WorldEventRequest(), out var error));
            Assert.IsNull(error);
            Assert.IsFalse(read, "no rule to apply: the landscape is never built");
            Assert.IsNull(composition.AreaPlan);
        }
    }
}
