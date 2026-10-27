using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-19 round: every event location reachable from the Town Network (townReach), the knights' cosmetic defense
    /// (protect.defendPose), and the chain's second stage pinned to "slay the champion" at a sky rift. Shares the builders in
    /// WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        // ---- Town Network reach ---------------------------------------------------------------------------------

        /// <summary>An outdoor spot in landblock 0xCE95 (Eastham's) at local (x, y).</summary>
        private static Position OutdoorSpot(float x = 100, float y = 100) => new Position(0xCE950022, x, y, 20, 0, 0, 0, 1, 0);

        private static List<WorldEventTown> TownsAround(Position spot, params (string Name, float Dx, float Dy)[] towns)
        {
            var g = spot.ToGlobal();

            return towns.Select(t => new WorldEventTown(t.Name, g.X + t.Dx, g.Y + t.Dy)).ToList();
        }

        [TestMethod]
        public void TownReach_OnlyASpotWithinTheWalkOfAPortalDropIsReachable()
        {
            var spot = OutdoorSpot();

            Assert.IsTrue(WorldEventTownReach.IsReachable(spot, TownsAround(spot, ("Eastham", 120, 0)), 250), "120 m from Eastham's drop");
            Assert.IsTrue(WorldEventTownReach.IsReachable(spot, TownsAround(spot, ("Far", 3000, 0), ("Eastham", 0, 249)), 250), "the nearest town counts");
            Assert.IsFalse(WorldEventTownReach.IsReachable(spot, TownsAround(spot, ("Eastham", 251, 0)), 250), "just past the walk");
            Assert.IsFalse(WorldEventTownReach.IsReachable(spot, new List<WorldEventTown>(), 250), "no Town Network loaded: nothing is reachable");
            Assert.IsFalse(WorldEventTownReach.IsReachable(new Position(0x01D90108, 10, 10, 0, 0, 0, 0, 1, 0), TownsAround(spot, ("Eastham", 0, 0)), 250),
                "indoors is never a Town Network spot");

            StringAssert.Contains(WorldEventTownReach.Describe(spot, TownsAround(spot, ("Eastham", 300, 400))), "Eastham's portal drop 500 m away");
        }

        [TestMethod]
        public void TownReach_ReRoll_TriesTheNearestTownsFirst_SkipsExcluded_AndStopsAtItsCount()
        {
            var spot = OutdoorSpot();
            var g = spot.ToGlobal();
            var towns = TownsAround(spot, ("Holtburg", 4000, 0), ("Eastham", 900, 0), ("Shoushi", 2000, 0), ("Yaraq", 9000, 0), ("Rithwic", 1500, 0));

            var reach = new TownReachDef { Towns = 3 };

            CollectionAssert.AreEqual(new[] { "Eastham", "Rithwic", "Shoushi" },
                WorldEventTownReach.RerollTowns(towns, g.X, g.Y, reach).Select(t => t.Name).ToList());

            CollectionAssert.AreEqual(new[] { "Rithwic", "Shoushi", "Holtburg" },
                WorldEventTownReach.RerollTowns(towns, g.X, g.Y, reach, new List<string> { "Eastham" }).Select(t => t.Name).ToList(),
                "a chained stage never re-rolls back to the town it came from");
        }

        [TestMethod]
        public void TownReach_Validates_AndAChainCarriesItToLaterStages()
        {
            Assert.IsNull(WorldEventTownReach.Validate(null));
            Assert.IsNull(WorldEventTownReach.Validate(new TownReachDef()));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { MaxWalk = 20 }));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { SpotMinDistance = 100, SpotMaxDistance = 90 }));
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { MaxWalk = 150, SpotMaxDistance = 160 }), "a re-roll must land within the walk");
            Assert.IsNotNull(WorldEventTownReach.Validate(new TownReachDef { Towns = 0 }));

            var own = new TownReachDef { MaxWalk = 300 };
            var inherited = new TownReachDef();

            Assert.AreSame(own, WorldEventTownReach.Effective(own, inherited));
            Assert.AreSame(inherited, WorldEventTownReach.Effective(null, inherited), "kill_boss has none of its own; the envoys' holds");
            Assert.IsNull(WorldEventTownReach.Effective(null, null));

            var first = new WorldEventRequest { TownReach = inherited };
            Assert.AreSame(inherited, WorldEventChain.NextRequest(first, new ChainDef(), 2, 2, 1, 1, OutdoorSpot(), "Eastham").TownReach);

            var json = HoldGoalsJson(SampleProtectJson).Replace(@"""mvpRule"": ""mostKills"",", @"""mvpRule"": ""mostKills"", ""townReach"": { ""maxWalk"": 250.0 },");
            var store = WorldEventAxisStore.Parse(null, null, json, null, null);
            Assert.AreEqual(250, store.Goals["hold_wards"].TownReach.MaxWalk, string.Join(" | ", store.Diagnostics));

            var bad = WorldEventAxisStore.Parse(null, null, json.Replace(@"""maxWalk"": 250.0", @"""maxWalk"": 5.0"), null, null);
            Assert.IsFalse(bad.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(bad.Diagnostics.Any(d => d.Contains("townReach.maxWalk")), string.Join(" | ", bad.Diagnostics));
        }

        // ---- the second stage: slay the champion ----------------------------------------------------------------

        [TestMethod]
        public void Chain_SecondStage_DefaultsToTheSkyRiftChampion_AndRandomStillRolls()
        {
            var next = WorldEventChain.NextRequest(new WorldEventRequest(), new ChainDef(), 2, 2, 7, 7, OutdoorSpot(), "Eastham");

            Assert.AreEqual("sky_rift", next.SourceId);
            Assert.AreEqual("kill_boss", next.GoalId);
            Assert.AreEqual("auto", next.BossId, "a named champion of the rolled family (World Events boss scale)");
            Assert.IsTrue(next.Random, "the family still rolls");
            Assert.AreEqual(0, next.FamilyIds.Count);

            var rolled = WorldEventChain.NextRequest(new WorldEventRequest(), new ChainDef { NextSource = "random", NextGoal = " Random ", NextBoss = "" }, 2, 2, 7, 7, OutdoorSpot(), "Eastham");

            Assert.IsNull(rolled.SourceId);
            Assert.IsNull(rolled.GoalId);
            Assert.IsNull(rolled.BossId);

            var custom = WorldEventChain.NextRequest(new WorldEventRequest(), new ChainDef { NextSource = "Ambush", NextGoal = "kill_count", NextBoss = "family_champion" }, 2, 2, 7, 7, OutdoorSpot(), "Eastham");

            Assert.AreEqual("ambush", custom.SourceId, "configurable");
            Assert.AreEqual("kill_count", custom.GoalId);
            Assert.AreEqual("family_champion", custom.BossId);
        }

        [TestMethod]
        public void Chain_SecondStageSettings_AreCheckedAgainstTheLoadedAxes()
        {
            var sources = new Dictionary<string, SourceThemeDef>
            {
                ["sky_rift"] = new SourceThemeDef { Id = "sky_rift", CompatibleGoals = new List<string> { "kill_count", "kill_boss" } },
                ["ambush"] = new SourceThemeDef { Id = "ambush", CompatibleGoals = new List<string> { "defend_envoys" } }
            };
            var goals = new List<string> { "kill_count", "kill_boss", "defend_envoys" };
            var bosses = new List<string> { "legate_vessaryn" };

            Assert.AreEqual(0, WorldEventChain.NextStageProblems(new ChainDef(), sources, goals, bosses).Count, "sky_rift + kill_boss + auto fit");
            Assert.AreEqual(0, WorldEventChain.NextStageProblems(new ChainDef { NextBoss = "legate_vessaryn" }, sources, goals, bosses).Count);
            Assert.AreEqual(0, WorldEventChain.NextStageProblems(new ChainDef { NextSource = "random", NextGoal = "random", NextBoss = "random" }, sources, goals, bosses).Count);

            Assert.IsTrue(WorldEventChain.NextStageProblems(new ChainDef { NextSource = "volcano" }, sources, goals, bosses).Single().Contains("nextSource 'volcano'"));
            Assert.IsTrue(WorldEventChain.NextStageProblems(new ChainDef { NextSource = "ambush" }, sources, goals, bosses).Single().Contains("does not support"));
            Assert.IsTrue(WorldEventChain.NextStageProblems(new ChainDef { NextBoss = "nobody" }, sources, goals, bosses).Single().Contains("nextBoss 'nobody'"));
        }

        [TestMethod]
        public void Chain_Broadcast_NamesTheChampion_OrSaysTheirChampion()
        {
            const string template = "The fight moves to {town}! {Family} gather near {location} behind {boss}, and you have {seconds} seconds to reach them.";

            Assert.AreEqual("The fight moves to Holtburg! The Rats gather near Holtburg (42.1N, 33.6E) behind Legate Vessaryn, and you have 120 seconds to reach them.",
                WorldEventChain.AnnounceLine(template, "the Rats", "Holtburg (42.1N, 33.6E)", 120, "Holtburg", "Legate Vessaryn"));
            StringAssert.Contains(WorldEventChain.AnnounceLine(template, "the Rats", "Holtburg (42.1N, 33.6E)", 120, "Holtburg"), "behind their champion");
        }

        [TestMethod]
        public void ShippedEnvoys_ReachableFromTheTownNetwork_AndChainToTheSkyRiftChampion_WhenPresent()
        {
            var path = ContentFile("dreamweave-content", "events", "defend_envoys.goal.json");

            if (path == null)
                Assert.Inconclusive("defend_envoys.goal.json not found next to the repo");

            var goal = WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null).Goals["defend_envoys"];

            Assert.IsNotNull(goal.TownReach, "every envoys location is a short walk from a portal drop");
            Assert.IsTrue(goal.TownReach.MaxWalk <= 300, $"a reasonable walk: {goal.TownReach.MaxWalk} m");
            Assert.IsTrue(goal.Chain.TownSpotMaxDistance + (goal.Protect.ClearArea?.SearchRadius ?? 0) <= goal.TownReach.MaxWalk,
                "a chained spot moved to open ground still stands within the walk");

            Assert.AreEqual("sky_rift", goal.Chain.NextSource);
            Assert.AreEqual("kill_boss", goal.Chain.NextGoal);
            Assert.AreEqual("auto", goal.Chain.NextBoss);
            Assert.AreEqual("standard", goal.Chain.RewardForStage(1));
            Assert.AreEqual("chain_final", goal.Chain.RewardForStage(2), "the champion stage pays the better reward");

            Assert.IsNotNull(goal.Protect.DefendPose, "the knights fight back (cosmetic)");
            Assert.IsNull(WorldEventHoldDefense.Validate(goal.Protect.DefendPose));

            foreach (var template in goal.Chain.Announce)
                foreach (var boss in new[] { "Legate Vessaryn", null })
                {
                    var line = WorldEventChain.AnnounceLine(template, "the Monougas and the Ruschks", "Holtburg (42.1N, 33.6E)", 120, "Holtburg", boss);

                    AssertAscii(WorldEventAnnouncer.Global(line));
                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), line);
                    Assert.IsTrue(Regex.Matches(line.Trim(), @"[.!?]+(\s|$)").Count <= 3, line);
                    Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, line);
                    StringAssert.Contains(line, "Holtburg");
                }

            // The live sources.json: sky_rift exists and supports kill_boss.
            var sourcesPath = ContentFile("ace-content", "events", "axes", "sources.json");
            var goalsPath = ContentFile("ace-content", "events", "axes", "goals.json");

            if (sourcesPath != null && goalsPath != null)
            {
                var store = WorldEventAxisStore.Parse(File.ReadAllText(sourcesPath), null, File.ReadAllText(goalsPath), null, null);

                Assert.IsTrue(store.Sources.TryGetValue("sky_rift", out var rift), "sky_rift is a loaded source");
                CollectionAssert.Contains(rift.CompatibleGoals, "kill_boss");

                var live = store.Goals["defend_envoys"];
                Assert.AreEqual("sky_rift", live.Chain.NextSource, "the live goal kept its second stage: " + string.Join(" | ", store.Diagnostics));
                Assert.IsNotNull(live.TownReach);
                Assert.IsNotNull(live.Protect.DefendPose);
            }
        }

        // ---- the knights' cosmetic defense ----------------------------------------------------------------------

        [TestMethod]
        public void DefendPose_PicksTheMonsterInReach_KeepsItWhileInReach_NeverOneOutside()
        {
            Assert.AreEqual(0u, WorldEventHoldDefense.PickFoe(new (uint, double)[0], 2.5, 0));
            Assert.AreEqual(0u, WorldEventHoldDefense.PickFoe(new[] { (11u, 2.6), (12u, 9.0) }, 2.5, 0), "nothing in reach: it faces outward");
            Assert.AreEqual(12u, WorldEventHoldDefense.PickFoe(new[] { (11u, 2.0), (12u, 0.5) }, 2.5, 0), "the nearest");
            Assert.AreEqual(11u, WorldEventHoldDefense.PickFoe(new[] { (11u, 2.0), (12u, 0.5) }, 2.5, 11), "keeps the one it faces");
            Assert.AreEqual(12u, WorldEventHoldDefense.PickFoe(new[] { (11u, 3.0), (12u, 0.5) }, 2.5, 11), "it left reach: the next");
            Assert.AreEqual(0u, WorldEventHoldDefense.PickFoe(new[] { (0u, 0.1), (13u, double.NaN) }, 2.5, 0));
        }

        [TestMethod]
        public void DefendPose_DrawsItsWeapon_FacesThenSwingsInRhythm_ThenTurnsBack()
        {
            var pose = new DefendPoseDef();

            Assert.AreEqual(WorldEventHoldDefense.PoseAction.DrawWeapon, WorldEventHoldDefense.Decide(false, false, 0, 10, 0, 0, true, pose), "the stance comes first");
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.None, WorldEventHoldDefense.Decide(true, false, 0, 10, 0, 0, true, pose), "weapon drawn, nobody near");
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.Face, WorldEventHoldDefense.Decide(true, true, 90, 10, 0, 0, true, pose), "off its facing: turn first");
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.Swing, WorldEventHoldDefense.Decide(true, true, 5, 10, 10, 10, false, pose));
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.None, WorldEventHoldDefense.Decide(true, true, 5, 10, 11, 10, false, pose), "waits for its rhythm");
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.None, WorldEventHoldDefense.Decide(true, false, 0, 12, 0, 10, false, pose), "a moment before it turns back");
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.FaceOutward, WorldEventHoldDefense.Decide(true, false, 0, 13, 0, 10, false, pose));

            var rng = new Random(5);

            for (var i = 0; i < 200; i++)
            {
                var next = WorldEventHoldDefense.NextSwingAt(100, 1.2, rng, pose) - 100;
                Assert.IsTrue(next >= 2.0 && next <= 3.0, $"a normal attack rhythm: {next:F2} s");
            }

            Assert.AreEqual(4.0, WorldEventHoldDefense.NextSwingAt(100, 4.0, rng, pose) - 100, 1e-9, "never before its swing has played");

            foreach (var stance in new[] { MotionStance.HandCombat, MotionStance.SwordShieldCombat, MotionStance.SwordCombat, MotionStance.Invalid })
                Assert.IsTrue(WorldEventHoldDefense.SwingCandidates(stance).Count > 0, $"{stance} has swings");

            Assert.IsNull(WorldEventHoldDefense.Validate(pose));
            Assert.IsNotNull(WorldEventHoldDefense.Validate(new DefendPoseDef { Reach = 10 }));
            Assert.IsNotNull(WorldEventHoldDefense.Validate(new DefendPoseDef { SwingSeconds = 3, SwingMaxSeconds = 2 }));
            Assert.IsNotNull(WorldEventHoldDefense.Validate(new DefendPoseDef { TurnBackSeconds = -1 }));
        }

        [TestMethod]
        public void DefendPose_AKnightsSwingDealsZeroDamage_AndNeverChangesTheMonstersTarget()
        {
            var protect = BuildProtect();
            protect.DefendPose = new DefendPoseDef();

            var evt = BuildHoldEvent(BuildHoldGoal(protect: protect), null);

            var knight = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            knight.WorldEventProtected = true;
            knight.P_WorldEvent = evt;

            var otherKnight = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            otherKnight.WorldEventProtected = true;
            otherKnight.P_WorldEvent = evt;

            var monster = NewRunMonster(evt);
            monster.AttackTarget = otherKnight;

            var health = monster.Health.Current;
            var rng = new Random(3);
            var actions = new List<WorldEventHoldDefense.PoseAction>();

            // Thirty seconds of the once-a-second tick with the monster in reach.
            for (var t = 0; t < 30; t++)
                actions.Add(knight.TickWorldEventDefendPose(monster, T0 + t, protect.DefendPose, rng));

            Assert.AreEqual(WorldEventHoldDefense.PoseAction.DrawWeapon, actions[0], "the weapon is drawn first");
            Assert.IsTrue(knight.WorldEventDefendSwings >= 8, $"it swings in rhythm: {knight.WorldEventDefendSwings} in 30 s");
            Assert.IsTrue(knight.WorldEventDefendSwings <= 15, $"and not faster than a normal attack: {knight.WorldEventDefendSwings} in 30 s");

            Assert.AreEqual(health, monster.Health.Current, "zero damage");
            Assert.IsFalse(monster.DamageHistory.HasDamager(knight), "no damage entry, so no kill credit or threat");
            Assert.AreEqual(0, monster.DamageHistory.TotalDamage.Count);
            Assert.AreSame(otherKnight, monster.AttackTarget, "the monster's target never changes");
            Assert.IsNull(knight.AttackTarget, "the knight never takes a target of its own");
            Assert.AreEqual(monster.Guid.Full, knight.WorldEventDefendFoe, "it faces the monster");

            // The monster leaves reach: it turns back outward after turnBackSeconds, and stays in its stance.
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.None, knight.TickWorldEventDefendPose(null, T0 + 31, protect.DefendPose, rng));
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.FaceOutward, knight.TickWorldEventDefendPose(null, T0 + 33, protect.DefendPose, rng));
            Assert.IsTrue(knight.WorldEventDefendFacingOutward);
            Assert.AreNotEqual(MotionStance.NonCombat, knight.CurrentMotionState.Stance, "weapon still drawn");
            Assert.AreEqual(0u, knight.WorldEventDefendFoe);

            // A goal without defendPose: the knight does nothing.
            Assert.AreEqual(WorldEventHoldDefense.PoseAction.None, otherKnight.TickWorldEventDefendPose(monster, T0, null, rng));
            Assert.AreEqual(0, otherKnight.WorldEventDefendSwings);
        }

        [TestMethod]
        public void DefendPose_ADeadMonsterIsNeverSwungAt()
        {
            var evt = BuildHoldEvent(BuildHoldGoal(protect: BuildProtect()), null);

            var knight = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            knight.WorldEventProtected = true;
            knight.P_WorldEvent = evt;

            var monster = NewRunMonster(evt);
            monster.Health.Current = 0;

            var pose = new DefendPoseDef();
            var rng = new Random(1);

            knight.TickWorldEventDefendPose(monster, T0, pose, rng);

            for (var t = 1; t < 10; t++)
                Assert.AreNotEqual(WorldEventHoldDefense.PoseAction.Swing, knight.TickWorldEventDefendPose(monster, T0 + t, pose, rng));

            Assert.AreEqual(0, knight.WorldEventDefendSwings);
        }
    }
}
