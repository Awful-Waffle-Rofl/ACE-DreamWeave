using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-game run 2 of the special-wave build: wave 1 was picked before the caravans entered the world, so its
    /// start line read "0 of 0" and a forced breach silently fell back to an ordinary wave; the seekers' roles
    /// were not visible in the log; and the small-group ring was too tight. Shares the builders in
    /// WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        private static HoldObjective SpawnerWiredObjective(WorldEvent evt, GoalDef goal) =>
            new HoldObjective(goal, () => evt.Spawner.ProtectedGuids, () => evt.Spawner.LiveProtectedGuids, () => T0, null,
                null, () => evt.HoldWaveCounts);

        // ---- 1. the first wave waits for the targets --------------------------------------------------------

        [TestMethod]
        public void FirstWave_WaitsForTheTargets_OnlyInAHoldRun()
        {
            Assert.IsTrue(WorldEvent.DefersFirstWave(BuildHoldGoal(protect: BuildProtect())));
            Assert.IsFalse(WorldEvent.DefersFirstWave(new GoalDef { Id = "kills", TypeKind = GoalType.KillCount }), "every other goal is unchanged");
            Assert.IsFalse(WorldEvent.DefersFirstWave(new GoalDef { Id = "bare", TypeKind = GoalType.Hold }), "a Hold with nothing to place");
            Assert.IsFalse(WorldEvent.DefersFirstWave(null));
        }

        [TestMethod]
        public void FirstWave_GoesOutOncePlacementResolves_OrAfterTheGrace()
        {
            var grace = HoldObjective.PlacementGraceSeconds;

            Assert.IsFalse(WorldEvent.HoldFirstWaveMayStart(false, 0, grace), "run 2: picked before the caravans existed");
            Assert.IsFalse(WorldEvent.HoldFirstWaveMayStart(false, grace - 0.1, grace));
            Assert.IsTrue(WorldEvent.HoldFirstWaveMayStart(true, 0.02, grace), "the batch reported back");
            Assert.IsTrue(WorldEvent.HoldFirstWaveMayStart(false, grace, grace), "a batch that never reports cannot stall the run");
        }

        [TestMethod]
        public void ProtectedPlacement_Resolves_EvenWhenNothingIsPlaced()
        {
            var protect = BuildProtect();
            var evt = BuildHoldEvent(BuildHoldGoal(protect: protect), null);

            Assert.IsFalse(evt.Spawner.ProtectedPlacementResolved, "nothing has been tried yet");

            // No landblock is held in a unit test, so the batch is skipped - and must still say it is done.
            evt.Spawner.SpawnProtected(evt, protect, new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0));
            Assert.IsTrue(evt.Spawner.ProtectedPlacementResolved);

            var noWcid = BuildHoldEvent(BuildHoldGoal(protect: BuildProtect()), null);
            var empty = BuildProtect();
            empty.Wcid = 0;

            noWcid.Spawner.SpawnProtected(noWcid, empty, new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0));
            Assert.IsTrue(noWcid.Spawner.ProtectedPlacementResolved, "a goal with no wcid resolves at once");
        }

        [TestMethod]
        public void WaveStartLine_CountsTheTargets_OnceTheyArePlaced()
        {
            var goal = WaveHoldGoal();
            goal.Protect.Noun = "caravan";
            goal.Protect.NounPlural = "caravans";

            var evt = BuildHoldEvent(goal, null);
            var objective = SpawnerWiredObjective(evt, goal);

            StringAssert.Contains(objective.WaveStartText(1), "0 of 0", "run 2: what wave 1 said when it went out too early");

            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());
            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            var line = objective.WaveStartText(1);

            StringAssert.Contains(line, "2 of 2 caravans");
            AssertAscii(line);
        }

        // ---- 2. a forced breach on wave 1 --------------------------------------------------------------------

        private static (WorldEvent Evt, HoldObjective Objective) BreachRun(HoldWaveType? forced)
        {
            var goal = WaveHoldGoal();
            goal.Protect.SpecialWaves = new List<SpecialWaveDef>
            {
                new SpecialWaveDef { Type = "breach", TypeKind = HoldWaveType.Breach, Weight = 1,
                    Announce = new List<string> { "One big push!" }, Lines = new List<string> { "They are massing on the {side} wagon!" } },
                new SpecialWaveDef { Type = "finale", TypeKind = HoldWaveType.Finale }
            };

            var evt = BuildHoldEvent(goal, null);
            evt.Request.ForcedWaveType = forced;

            return (evt, SpawnerWiredObjective(evt, goal));
        }

        private static WavePick SixTusks() => new WavePick(new List<uint> { 1, 1, 2, 2, 3, 3 }, new List<uint>());

        [TestMethod]
        public void ForcedBreach_Wave1_WithTargetsStanding_IsABreach()
        {
            var (evt, objective) = BreachRun(HoldWaveType.Breach);

            var a = BuildHoldCreature();
            var b = BuildHoldCreature();
            evt.Spawner.AdoptProtectedForTest(a);
            evt.Spawner.AdoptProtectedForTest(b);

            var plan = evt.BuildHoldWavePlan(objective, 1, SixTusks());

            Assert.IsNotNull(plan, "--wave-type breach applies to wave 1");
            Assert.AreEqual(HoldWaveType.Breach, plan.Type);
            CollectionAssert.Contains(new[] { a.Guid.Full, b.Guid.Full }, plan.BreachTargetGuid, "one of the standing caravans");
            Assert.IsFalse(string.IsNullOrEmpty(plan.BreachSide));
        }

        [TestMethod]
        public void ForcedBreach_BeforeAnyTargetStands_FallsBackToOrdinary()
        {
            // What run 2's wave 1 hit. With the first wave now waiting for placement this only happens when
            // nothing could be placed at all; it is logged as a warning rather than passing silently.
            var (evt, objective) = BreachRun(HoldWaveType.Breach);

            Assert.IsNull(evt.BuildHoldWavePlan(objective, 1, SixTusks()));
        }

        [TestMethod]
        public void ForcedBreach_LastWave_IsStillTheFinale()
        {
            var (evt, objective) = BreachRun(HoldWaveType.Breach);
            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            var plan = evt.BuildHoldWavePlan(objective, objective.TargetWaves, SixTusks());

            Assert.AreEqual(HoldWaveType.Finale, plan?.Type);
        }

        [TestMethod]
        public void NoForcedType_Wave1_IsOrdinary()
        {
            var (evt, objective) = BreachRun(null);
            evt.Spawner.AdoptProtectedForTest(BuildHoldCreature());

            Assert.IsNull(evt.BuildHoldWavePlan(objective, 1, SixTusks()), "chance 0 and wave 1: ordinary");
        }

        // ---- 3. who is a seeker, and seekers stay on the targets ---------------------------------------------

        [TestMethod]
        public void WaveRoleSummary_CountsSeekersHuntersAndOthers()
        {
            var roles = new List<(bool, bool, uint)>
            {
                (true, false, 0u), (true, false, 0u), (true, false, 0u), (true, false, 0u), (false, false, 0u), (false, false, 0u)
            };

            Assert.AreEqual("hold wave 1 roles: seekers=4 hunters=0 others=2", WorldEventHoldRules.WaveRoleSummary(1, roles),
                "run 2 wave 1: four went for the caravans, two for the player");

            var breach = new List<(bool, bool, uint)>
            {
                (true, false, 0x8000037Cu), (true, false, 0x8000037Cu), (true, false, 0x8000037Cu), (false, true, 0u), (false, false, 0u)
            };

            Assert.AreEqual("hold wave 2 roles: seekers=3 hunters=1 others=1 breach=[0x8000037Cx3]", WorldEventHoldRules.WaveRoleSummary(2, breach));
            Assert.AreEqual("hold wave 3 roles: seekers=0 hunters=0 others=0", WorldEventHoldRules.WaveRoleSummary(3, null));
        }

        [TestMethod]
        public void SeekerStrayed_OnlyASeekerOffTheTargets_WhileOneStands()
        {
            Assert.IsTrue(WorldEventHoldRules.SeekerStrayed(true, false, true), "a seeker on a player");
            Assert.IsFalse(WorldEventHoldRules.SeekerStrayed(true, true, true), "a seeker on a caravan");
            Assert.IsFalse(WorldEventHoldRules.SeekerStrayed(false, false, true), "an ordinary monster on a player");
            Assert.IsFalse(WorldEventHoldRules.SeekerStrayed(true, false, false), "no caravan left to go back to");
        }

        [TestMethod]
        public void RestoreSeekerLock_LeavesNonSeekersAndTauntsAlone()
        {
            var monster = BuildHoldCreature();
            var player = BuildHoldCreature();

            monster.AttackTarget = player;
            Assert.IsFalse(monster.TryRestoreWorldEventSeekerLock(), "not a seeker");
            Assert.AreSame(player, monster.AttackTarget);

            monster.WorldEventSeeker = true;
            monster.TauntTarget = player;
            monster.TauntExpiry = double.MaxValue;
            Assert.IsFalse(monster.TryRestoreWorldEventSeekerLock(), "a taunt may hold a seeker elsewhere");
            Assert.AreSame(player, monster.AttackTarget);

            var caravan = BuildHoldCreature();
            caravan.WorldEventProtected = true;
            monster.TauntTarget = null;
            monster.AttackTarget = caravan;
            Assert.IsFalse(monster.TryRestoreWorldEventSeekerLock(), "already on a caravan");
            Assert.AreSame(caravan, monster.AttackTarget);
        }

        // ---- 4. layout and spawn clearance ---------------------------------------------------------------------

        private static GoalDef LayoutGoal(double spawnRadius)
        {
            var protect = BuildProtect(countBase: 3, countCap: 3);
            protect.Radius = 16.0;
            protect.SpawnRadius = spawnRadius;
            protect.CountByParticipants = new List<TargetLayoutDef>
            {
                new TargetLayoutDef { MinPlayers = 0, Count = 2, Radius = 14.0 },
                new TargetLayoutDef { MinPlayers = 4, Count = 3, Radius = 16.0 }
            };

            return BuildHoldGoal(protect: protect);
        }

        [TestMethod]
        public void Layout_ShippedRings_AreWideEnoughToRunBetween()
        {
            var protect = LayoutGoal(26).Protect;

            Assert.AreEqual((2, 14.0), protect.ResolveLayout(1));
            Assert.AreEqual((3, 16.0), protect.ResolveLayout(4));

            // Two caravans on a 14 m ring stand 28 m apart; three on 16 m stand about 27.7 m apart.
            var two = WorldEventHoldRules.RingOffsets(2, 14.0);
            var three = WorldEventHoldRules.RingOffsets(3, 16.0);

            double Gap(IReadOnlyList<WorldEventHoldRules.RingOffset> ring) =>
                System.Math.Sqrt(System.Math.Pow(ring[0].Dx - ring[1].Dx, 2) + System.Math.Pow(ring[0].Dy - ring[1].Dy, 2));

            Assert.IsTrue(Gap(two) >= 27.9, $"2 caravans {Gap(two):F1} m apart");
            Assert.IsTrue(Gap(three) >= 27.0, $"3 caravans {Gap(three):F1} m apart");
        }

        [TestMethod]
        public void SpawnRadius_AlwaysOutsideTheWidestRing()
        {
            var theme = new SourceThemeDef { GeometryRadius = 45f };

            Assert.AreEqual(26f, WorldEvent.ResolveSpawnRadius(theme, LayoutGoal(26)), 1e-4, "the shipped value");
            Assert.AreEqual(26f, WorldEvent.ResolveSpawnRadius(theme, LayoutGoal(20)), 1e-4, "a tight spawnRadius is raised to ring + 10");
            Assert.AreEqual(35f, WorldEvent.ResolveSpawnRadius(theme, LayoutGoal(35)), 1e-4, "a wider one is kept");
            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(theme, LayoutGoal(0)), 1e-4, "no spawnRadius: the source's own radius");
            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(theme, new GoalDef { TypeKind = GoalType.KillCount }), 1e-4, "other goals unchanged");
        }

        [TestMethod]
        public void GoalsJson_TightSpawnRadius_IsNoted()
        {
            var json = WithSpecials(SpecialWavesJson).Replace(@"""spawnRadius"": 25.0", @"""spawnRadius"": 15.0");

            var store = WorldEventAxisStore.Parse(null, null, json, null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), "a note, not a reason to drop the goal");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.spawnRadius 15") && d.Contains("21")), string.Join(" | ", store.Diagnostics));
        }
    }
}
