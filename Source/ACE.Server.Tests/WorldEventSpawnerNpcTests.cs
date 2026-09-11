using System;
using System.Linq;
using System.Numerics;

using ACE.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-15 spawn-time npc path in <see cref="WorldEventSpawner"/>. Only the pure
    /// members are exercised (TECH-DESIGN D6): the placement helpers and the kind rules. The spawn loop
    /// itself needs a live landblock and a real Creature and therefore goes to the verify queue, not CI.
    /// </summary>
    [TestClass]
    public class WorldEventSpawnerNpcTests
    {
        // Same outdoor cell shape WorldEventGeometryTests and the decor tests use: cell bits below 0x100
        // (outdoors) and non-zero, so the Position constructor does not rebase X/Y through SetPosition.
        private const uint OutdoorCell = 0x016C0025;

        private const float CentreX = 96f;
        private const float CentreY = 96f;
        private const float CentreZ = 42f;

        private const uint TestInstance = 7u;

        private static Position Centre()
        {
            return new Position(OutdoorCell, CentreX, CentreY, CentreZ, 0f, 0f, 0f, 1f, TestInstance);
        }

        // ---- heading -----------------------------------------------------------------------------------

        /// <summary>
        /// Yaw 0 is the identity rotation, and Position.GetCurrentDir transforms UnitY by it
        /// (Source/ACE.Entity/Position.cs:102-105), so an npc at yaw 0 faces +Y - north.
        /// </summary>
        [TestMethod]
        public void NpcRotation_Yaw0_IsIdentityAndFacesNorth()
        {
            var rotation = WorldEventSpawner.NpcRotation(0f);

            Assert.AreEqual(1f, rotation.W, 0.00001f);
            Assert.AreEqual(0f, rotation.X, 0.00001f);
            Assert.AreEqual(0f, rotation.Y, 0.00001f);
            Assert.AreEqual(0f, rotation.Z, 0.00001f);

            var facing = Vector3.Transform(Vector3.UnitY, rotation);

            Assert.AreEqual(0f, facing.X, 0.00001f);
            Assert.AreEqual(1f, facing.Y, 0.00001f, "yaw 0 faces +Y, i.e. north");
            Assert.AreEqual(0f, facing.Z, 0.00001f);
        }

        /// <summary>
        /// Positive yaw turns counter-clockwise seen from above, so yaw 90 faces -X - west. That is the
        /// heading the handoff asked for from the attendant standing due EAST of the centre.
        /// </summary>
        [TestMethod]
        public void NpcRotation_Yaw90_FacesWest()
        {
            var facing = Vector3.Transform(Vector3.UnitY, WorldEventSpawner.NpcRotation(90f));

            Assert.AreEqual(-1f, facing.X, 0.00001f, "yaw 90 faces -X, i.e. west");
            Assert.AreEqual(0f, facing.Y, 0.00001f);
        }

        /// <summary>
        /// The three quaternions the reviewer read off the live scene on 2026-08-16, in (w, x, y, z) order,
        /// reproduced from the yaw values sources.json now ships. Pinned to 1e-5: these are the numbers the
        /// approved picture was made of, and a sign flip anywhere in the composition shows up here.
        /// </summary>
        [TestMethod]
        public void NpcRotation_TheThreeLozHeadings_MatchTheReviewedQuaternions()
        {
            var expected = new[]
            {
                (yaw: 90f, w: 0.707107f, z: 0.707107f),
                (yaw: 210f, w: -0.258819f, z: 0.965926f),
                (yaw: 330f, w: -0.965926f, z: 0.258819f)
            };

            foreach (var (yaw, w, z) in expected)
            {
                var rotation = WorldEventSpawner.NpcRotation(yaw);

                Assert.AreEqual(w, rotation.W, 0.00001f, $"yaw {yaw} W");
                Assert.AreEqual(0f, rotation.X, 0.00001f, $"yaw {yaw} X");
                Assert.AreEqual(0f, rotation.Y, 0.00001f, $"yaw {yaw} Y");
                Assert.AreEqual(z, rotation.Z, 0.00001f, $"yaw {yaw} Z");
            }
        }

        /// <summary>
        /// The rule the yaw values were derived from, checked end to end rather than restated: an attendant
        /// at polar angle a takes yaw a + 90, and the resulting facing points back at the centre.
        /// </summary>
        [TestMethod]
        public void NpcRotation_YawIsPolarAnglePlus90_AndFacesTheCentre()
        {
            var attendants = new[]
            {
                (dx: 4.8f, dy: 0f, yaw: 90f),
                (dx: -2.4f, dy: 4.157f, yaw: 210f),
                (dx: -2.4f, dy: -4.157f, yaw: 330f)
            };

            foreach (var (dx, dy, yaw) in attendants)
            {
                var facing = Vector3.Transform(Vector3.UnitY, WorldEventSpawner.NpcRotation(yaw));

                // The direction from the attendant back to the centre is the negated offset, normalised.
                var toCentre = Vector3.Normalize(new Vector3(-dx, -dy, 0f));

                Assert.AreEqual(toCentre.X, facing.X, 0.001f, $"attendant at ({dx}, {dy}) must face the centre in X");
                Assert.AreEqual(toCentre.Y, facing.Y, 0.001f, $"attendant at ({dx}, {dy}) must face the centre in Y");
            }
        }

        // ---- position ----------------------------------------------------------------------------------

        [TestMethod]
        public void NpcPosition_AppliesTheOffsetAndKeepsTheInstance()
        {
            var centre = Centre();

            var position = WorldEventSpawner.NpcPosition(centre, 4.8f, 0f, 0f, 90f);

            Assert.AreEqual(CentreX + 4.8f, position.PositionX, 0.0001f);
            Assert.AreEqual(CentreY, position.PositionY, 0.0001f);
            Assert.AreEqual(CentreZ, position.PositionZ, 0.0001f);
            Assert.AreEqual(centre.Instance, position.Instance, "the instance must follow the anchor");
            Assert.AreEqual(centre.InstancedLandblock, position.InstancedLandblock);
            Assert.AreEqual(WorldEventSpawner.NpcRotation(90f), position.Rotation);
        }

        [TestMethod]
        public void NpcPosition_AppliesDz()
        {
            var position = WorldEventSpawner.NpcPosition(Centre(), 0f, 0f, 1.75f, 0f);

            Assert.AreEqual(CentreZ + 1.75f, position.PositionZ, 0.0001f);
        }

        [TestMethod]
        public void NpcPosition_DoesNotMutateTheCentre()
        {
            var centre = Centre();

            WorldEventSpawner.NpcPosition(centre, -2.4f, 4.157f, 0f, 210f);

            Assert.AreEqual(CentreX, centre.PositionX, 0.0001f, "the shared anchor position must be copied, not moved");
            Assert.AreEqual(CentreY, centre.PositionY, 0.0001f);
            Assert.AreEqual(Quaternion.Identity, centre.Rotation, "the shared anchor rotation must not be turned");
            Assert.AreEqual(OutdoorCell, centre.Cell, "the shared anchor cell must not be rebased");
        }

        /// <summary>
        /// The reason npcs use SetPosition where decor writes PositionZ raw: X and Y move here, and a land
        /// cell is 24 m, so a few metres of offset can land in a different cell of the same landblock. The
        /// LANDBLOCK is unchanged (which is what the held-landblock guard cares about), the CELL is not.
        /// </summary>
        [TestMethod]
        public void NpcPosition_ReResolvesTheLandCell_WhenTheOffsetCrossesOne()
        {
            var centre = Centre();

            // 96 is 4 cells in (24 m each); -2.4 m puts this into cell column 3 instead of 4.
            var position = WorldEventSpawner.NpcPosition(centre, -2.4f, 0f, 0f, 0f);

            Assert.AreNotEqual(centre.Cell, position.Cell, "an offset across a cell boundary must re-resolve the cell");
            Assert.AreEqual(centre.InstancedLandblock, position.InstancedLandblock, "but it is still the same landblock");
            Assert.AreEqual(CentreX - 2.4f, position.PositionX, 0.0001f, "and the coordinates are unchanged inside the block");
        }

        [TestMethod]
        public void NpcPosition_NullCentre_ReturnsNull()
        {
            Assert.IsNull(WorldEventSpawner.NpcPosition(null, 1f, 1f, 0f, 0f));
        }

        /// <summary>The three attendants land on the 4.8 m circle the scene was reviewed at.</summary>
        [TestMethod]
        public void NpcPosition_TheThreeAttendants_StandOnA48mTriangle()
        {
            var centre = Centre();

            var offsets = new[]
            {
                (dx: 4.8f, dy: 0f),
                (dx: -2.4f, dy: 4.157f),
                (dx: -2.4f, dy: -4.157f)
            };

            foreach (var (dx, dy) in offsets)
            {
                var position = WorldEventSpawner.NpcPosition(centre, dx, dy, 0f, 0f);

                var radius = Math.Sqrt(Math.Pow(position.PositionX - CentreX, 2)
                                       + Math.Pow(position.PositionY - CentreY, 2));

                Assert.AreEqual(4.8, radius, 0.005, $"attendant at ({dx}, {dy})");
            }
        }

        // ---- kind rules --------------------------------------------------------------------------------

        /// <summary>
        /// An npc IS a Creature, so unlike decor it does NOT get the "may be something else" exemption - a
        /// Generic wcid in a theme's npcs list is refused by SpawnNpcs with the wave path's log line.
        /// </summary>
        [TestMethod]
        public void RequiresCreature_IsTrueForNpc()
        {
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Npc));
            Assert.IsFalse(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Decor),
                "decor is still the only kind that may be a non-Creature");
        }

        /// <summary>An attendant standing at the edge of the scene is not trash pressure.</summary>
        [TestMethod]
        public void CountsAsWavePressure_IsFalseForNpc()
        {
            Assert.IsFalse(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Npc));
        }

        /// <summary>
        /// The load-bearing rule of the whole WP: CountsAsLive false is what routes an npc into its own list
        /// in Adopt AND stops the P_WorldEvent back-reference from being written, which is in turn what keeps
        /// an npc death out of Creature.Die's world-event hook.
        /// </summary>
        [TestMethod]
        public void CountsAsLive_IsFalseForNpcAndDecorOnly()
        {
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Npc));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Decor));

            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Wave));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Source));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Boss));
        }

        /// <summary>
        /// An npc is never a cache (PlaceNpcs writes no PropertyBool at all), so isCache is false for it and
        /// BOTH DestroyAll passes take it - the creature pass at Finish is what clears the attendants.
        /// </summary>
        [TestMethod]
        public void DestroyedByPass_TakesNpcsOnBothPasses()
        {
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: false, includeCaches: false),
                "the creature pass at Finish must destroy the attendants");
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: false, includeCaches: true),
                "the final sweep must destroy them too");
        }

        [TestMethod]
        public void FreshSpawner_ReportsNoNpcs()
        {
            var spawner = new WorldEventSpawner();

            Assert.AreEqual(0, spawner.NpcCount);
            Assert.AreEqual(0, spawner.LiveCount);
            Assert.AreEqual(0, spawner.LiveTotal);
            Assert.IsFalse(spawner.IsNpcGuid(0x80000001));
            Assert.IsFalse(spawner.IsDecorGuid(0x80000001));
        }

        /// <summary>
        /// An npc guid never enters the objective inputs: SourceGuids stays empty and BossGuid stays 0
        /// whatever a theme's npcs list contains, because Adopt returns before either is touched.
        /// </summary>
        [TestMethod]
        public void FreshSpawner_HasNoObjectiveInputs()
        {
            var spawner = new WorldEventSpawner();

            Assert.AreEqual(0, spawner.SourceGuids.Count);
            Assert.AreEqual(0u, spawner.BossGuid);
            Assert.IsFalse(spawner.AllSourcesDead, "no source placed must never read as 'already finished'");
        }

        // ---- objective interaction ---------------------------------------------------------------------

        /// <summary>
        /// The npc counterpart of WorldEventSpawnerDecorTests' decor-guid test. As of WP-16,
        /// WorldEventObjectiveFactory hands KillCountObjective the UNION of SourceGuids and NpcGuids (see
        /// WorldEventObjectiveFactory.ExclusionGuids), so a raw npc guid handed to OnDeathCore through that
        /// union is now excluded - this test drives OnDeathCore directly against an inline exclusion func
        /// shaped like that union, the same way the WP-15 version of this test drove it.
        ///
        /// The withheld P_WorldEvent back-reference remains the PRIMARY guard, and this exclusion is belt and
        /// braces on top of it: an npc is adopted WITHOUT P_WorldEvent (WorldEventSpawner.Adopt returns in
        /// the CountsAsLive-false branch before it is set), and
        /// Source/ACE.Server/WorldObjects/Creature_Death.cs:130 calls WorldEventManager.OnEventCreatureDied
        /// only when BOTH P_WorldEvent is non-null AND the WorldEventId stamp is present. An npc carries the
        /// stamp and not the reference, so its death stops there and never reaches OnDeathCore at all in
        /// practice. WorldEventManager.OnEventCreatureDied re-checks the same reference
        /// (WorldEventManager.cs:356) as a second gate.
        /// </summary>
        [TestMethod]
        public void KillCountObjective_ExcludesNpcGuids_ViaTheFactoryExclusion()
        {
            var goal = new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = MvpRule.MostKills,
                Count = new ScaledCount { Base = 5, PerParticipant = 0, Cap = 5 },
                ProgressTemplate = "{killed} of {target} slain."
            };

            // The shape WorldEventObjectiveFactory.ExclusionGuids now builds: SourceGuids unioned with
            // NpcGuids, driven here as a plain inline func rather than through a live spawner (D6).
            var sourceGuids = new[] { 0x8000BEEFu };
            var npcGuids = new[] { 0x8000CAFEu };

            var objective = new KillCountObjective(goal, () => 0,
                () => sourceGuids.Concat(npcGuids).ToArray(), new WorldEventParticipation(() => 0d));

            Assert.AreEqual("0 of 5 slain.", objective.ProgressText);

            // A death report for the npc guid specifically (not the source guid) must not advance the count
            // now that the factory unions the two sets.
            objective.OnDeathCore(0x8000CAFEu, 1002639u, wasEventCreature: true, "Someone", "Someone");

            Assert.AreEqual("0 of 5 slain.", objective.ProgressText,
                "an npc guid reaching OnDeathCore through the union exclusion must not be counted");

            // A genuinely unrelated event-creature guid still counts, so the exclusion is not swallowing
            // everything.
            objective.OnDeathCore(0x80001234u, 1002604u, wasEventCreature: true, "Someone", "Someone");

            Assert.AreEqual("1 of 5 slain.", objective.ProgressText);
        }
    }
}
