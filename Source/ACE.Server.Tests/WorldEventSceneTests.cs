using System;
using System.IO;
using System.Linq;

using ACE.Server.Command.Handlers;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for WP-22 "/worldevent scene" - WorldEventScenePreview.RotateOffset/BuildPlan (pure,
    /// D6) against hand-built themes and the shipped element_portal_fire/rift axis content, plus
    /// WorldEventCommands.TryParseSceneArgs. No live WorldObject/Landblock/Session anywhere here - Spawn/
    /// Clear/List need a running server and are not covered by this file (see Docs/VERIFY-QUEUE.md).
    /// </summary>
    [TestClass]
    public class WorldEventSceneTests
    {
        // ---- locate the shipped axis files, same helper WorldEventAxisStoreTests uses -----------------

        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sources.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static WorldEventAxisStore LoadShippedStore()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            var sources = File.ReadAllText(Path.Combine(dir, "sources.json"));
            var families = File.ReadAllText(Path.Combine(dir, "families.json"));
            var goals = File.ReadAllText(Path.Combine(dir, "goals.json"));
            var rewards = File.ReadAllText(Path.Combine(dir, "rewards.json"));
            var anchors = File.ReadAllText(Path.Combine(dir, "anchors.json"));

            return WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);
        }

        // -----------------------------------------------------------------------------------------------
        // RotateOffset
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void RotateOffset_Yaw90_EastBecomesNorth()
        {
            var (dx, dy) = WorldEventScenePreview.RotateOffset(6.5f, 0f, 90f);

            Assert.AreEqual(0f, dx, 1e-4f);
            Assert.AreEqual(6.5f, dy, 1e-4f);
        }

        [TestMethod]
        public void RotateOffset_Yaw0_IsIdentity()
        {
            var (dx, dy) = WorldEventScenePreview.RotateOffset(6.5f, 0f, 0f);

            Assert.AreEqual(6.5f, dx, 1e-4f);
            Assert.AreEqual(0f, dy, 1e-4f);
        }

        [TestMethod]
        public void RotateOffset_Yaw180_Negates()
        {
            var (dx, dy) = WorldEventScenePreview.RotateOffset(6.5f, 0f, 180f);

            Assert.AreEqual(-6.5f, dx, 1e-4f);
            Assert.AreEqual(0f, dy, 1e-4f);
        }

        // -----------------------------------------------------------------------------------------------
        // BuildPlan - element_portal_fire (fixed-offset objectives.json path)
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void BuildPlan_ElementPortalFire_Decor()
        {
            var store = LoadShippedStore();
            var theme = store.Sources["element_portal_fire"];

            var plan = WorldEventScenePreview.BuildPlan(theme, 1, 0f);
            var decor = plan.Where(e => e.Kind == ScenePlanKind.Decor).ToList();

            // Round 2: five pitched portal layers plus four snapped scenery columns.
            Assert.AreEqual(9, decor.Count);
            var layers = decor.Where(e => e.Pitch > 0f).ToList();
            Assert.AreEqual(5, layers.Count);
            CollectionAssert.AreEquivalent(new[] { 6.9f, 5.5f, 4.1f, 2.8f, 1.7f }, layers.Select(e => e.Scale).ToArray());
            Assert.AreEqual(4, decor.Count(e => e.Pitch == 0f && e.Snap && e.Wcid == 1002665));
        }

        [TestMethod]
        public void BuildPlan_ElementPortalFire_Npcs()
        {
            // Round 2 (2026-08-16): the scenery columns moved from the npcs list to terrain-snapped decor,
            // so an element theme places no npcs at all.
            var store = LoadShippedStore();
            var theme = store.Sources["element_portal_fire"];

            var plan = WorldEventScenePreview.BuildPlan(theme, 1, 0f);
            var npcs = plan.Where(e => e.Kind == ScenePlanKind.Npc).ToList();

            Assert.AreEqual(0, npcs.Count);
        }

        [TestMethod]
        public void BuildPlan_ElementPortalFire_Objectives_HealthResolvesFromParticipants()
        {
            var store = LoadShippedStore();
            var theme = store.Sources["element_portal_fire"];

            // objectiveHealth is { base: 8000, perParticipant: 2500, cap: 80000 } (ScaledCount.Resolve:
            // base + ceil(perParticipant * participants), floored at base, capped). Resolve(0) = 8000,
            // Resolve(2) = 8000 + ceil(2500*2) = 13000 - both verified directly against ScaledCount.cs and
            // sources.json's element_portal_fire entry.
            var planZero = WorldEventScenePreview.BuildPlan(theme, 0, 0f);
            var objectivesZero = planZero.Where(e => e.Kind == ScenePlanKind.Objective).ToList();

            Assert.AreEqual(2, objectivesZero.Count);
            Assert.IsTrue(objectivesZero.All(e => e.Wcid == 1002660));
            Assert.IsTrue(objectivesZero.All(e => e.Health == 8000));

            var planTwo = WorldEventScenePreview.BuildPlan(theme, 2, 0f);
            var objectivesTwo = planTwo.Where(e => e.Kind == ScenePlanKind.Objective).ToList();

            Assert.IsTrue(objectivesTwo.All(e => e.Health == 13000));
        }

        [TestMethod]
        public void BuildPlan_ElementPortalFire_Objectives_SceneYaw0_AreAtPlusMinus6_5East()
        {
            var store = LoadShippedStore();
            var theme = store.Sources["element_portal_fire"];

            var plan = WorldEventScenePreview.BuildPlan(theme, 1, 0f);
            var objectives = plan.Where(e => e.Kind == ScenePlanKind.Objective)
                .OrderBy(e => e.Dx).ToList();

            Assert.AreEqual(2, objectives.Count);
            Assert.AreEqual(-6.5f, objectives[0].Dx, 1e-4f);
            Assert.AreEqual(0f, objectives[0].Dy, 1e-4f);
            Assert.AreEqual(6.5f, objectives[1].Dx, 1e-4f);
            Assert.AreEqual(0f, objectives[1].Dy, 1e-4f);
        }

        [TestMethod]
        public void BuildPlan_ElementPortalFire_Objectives_SceneYaw90_AreAtPlusMinus6_5North()
        {
            var store = LoadShippedStore();
            var theme = store.Sources["element_portal_fire"];

            var plan = WorldEventScenePreview.BuildPlan(theme, 1, 90f);
            var objectives = plan.Where(e => e.Kind == ScenePlanKind.Objective)
                .OrderBy(e => e.Dy).ToList();

            Assert.AreEqual(2, objectives.Count);

            foreach (var objective in objectives)
                Assert.AreEqual(0f, objective.Dx, 1e-3f);

            Assert.AreEqual(-6.5f, objectives[0].Dy, 1e-3f);
            Assert.AreEqual(6.5f, objectives[1].Dy, 1e-3f);
        }

        // -----------------------------------------------------------------------------------------------
        // BuildPlan - rift (legacy objectiveWcid/geometry-anchor path)
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void BuildPlan_Rift_FourObjectivesOnA25mRing_NoHealth()
        {
            var store = LoadShippedStore();
            var theme = store.Sources["rift"];

            var plan = WorldEventScenePreview.BuildPlan(theme, 4, 0f);
            var objectives = plan.Where(e => e.Kind == ScenePlanKind.Objective).ToList();

            Assert.AreEqual(4, objectives.Count);
            Assert.IsTrue(objectives.All(e => e.Wcid == 1002603));
            Assert.IsTrue(objectives.All(e => e.Health == null), "rift sets no objectiveHealth");

            foreach (var objective in objectives)
            {
                var distance = Math.Sqrt(objective.Dx * objective.Dx + objective.Dy * objective.Dy);
                Assert.AreEqual(25.0, distance, 1e-3, $"objective at ({objective.Dx},{objective.Dy}) should sit on the 25m ring");
            }

            // decor/npcs are empty for rift - only the objective anchors are placed.
            Assert.AreEqual(0, plan.Count(e => e.Kind == ScenePlanKind.Decor));
            Assert.AreEqual(0, plan.Count(e => e.Kind == ScenePlanKind.Npc));
        }

        [TestMethod]
        public void BuildPlan_Rift_TwoCallsWithSameArgsGiveIdenticalOffsets()
        {
            // The legacy ObjectiveWcid/geometry-anchor path (rift's Ring geometry) must be genuinely
            // deterministic - not merely "no live WorldObject" - so a repeated "scene rift" places the
            // rifts on the same compass points every time rather than a fresh random ring each call.
            var store = LoadShippedStore();
            var theme = store.Sources["rift"];

            var planA = WorldEventScenePreview.BuildPlan(theme, 4, 0f)
                .Where(e => e.Kind == ScenePlanKind.Objective).OrderBy(e => e.Dx).ThenBy(e => e.Dy).ToList();
            var planB = WorldEventScenePreview.BuildPlan(theme, 4, 0f)
                .Where(e => e.Kind == ScenePlanKind.Objective).OrderBy(e => e.Dx).ThenBy(e => e.Dy).ToList();

            Assert.AreEqual(planA.Count, planB.Count);
            Assert.IsTrue(planA.Count > 0);

            for (var i = 0; i < planA.Count; i++)
            {
                Assert.AreEqual(planA[i].Dx, planB[i].Dx, 1e-6f);
                Assert.AreEqual(planA[i].Dy, planB[i].Dy, 1e-6f);
                Assert.AreEqual(planA[i].Yaw, planB[i].Yaw, 1e-4f);
            }
        }

        // -----------------------------------------------------------------------------------------------
        // BuildPlan - degenerate input
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void BuildPlan_NullTheme_ReturnsEmptyPlan()
        {
            var plan = WorldEventScenePreview.BuildPlan(null, 1, 0f);

            Assert.AreEqual(0, plan.Count);
        }

        // -----------------------------------------------------------------------------------------------
        // TryParseSceneArgs
        // -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void SceneArgs_NoTokens_Errors()
        {
            var ok = WorldEventCommands.TryParseSceneArgs(Array.Empty<string>(), out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("a source theme id is required", error);
        }

        [TestMethod]
        public void SceneArgs_LeadingFlag_Errors()
        {
            var ok = WorldEventCommands.TryParseSceneArgs(new[] { "--players", "2" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("a source theme id is required", error);
        }

        [TestMethod]
        public void SceneArgs_HappyPath_DefaultsPlayersToOneAndYawToZero()
        {
            var ok = WorldEventCommands.TryParseSceneArgs(new[] { "Element_Portal_Fire" }, out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual("element_portal_fire", parsed.SourceId);
            Assert.AreEqual(1, parsed.Players);
            Assert.AreEqual(0f, parsed.Yaw);
        }

        [TestMethod]
        public void SceneArgs_HappyPath_PlayersAndYaw()
        {
            var ok = WorldEventCommands.TryParseSceneArgs(
                new[] { "rift", "--players", "3", "--yaw", "-90.5" }, out var parsed, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual("rift", parsed.SourceId);
            Assert.AreEqual(3, parsed.Players);
            Assert.AreEqual(-90.5f, parsed.Yaw, 1e-4f);
        }

        [TestMethod]
        public void SceneArgs_NegativePlayers_Errors()
        {
            var ok = WorldEventCommands.TryParseSceneArgs(
                new[] { "rift", "--players", "-1" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("--players needs a non-negative integer", error);
        }

        [TestMethod]
        public void SceneArgs_UnknownFlag_Errors()
        {
            var ok = WorldEventCommands.TryParseSceneArgs(
                new[] { "rift", "--bogus" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.AreEqual("unknown flag --bogus", error);
        }
    }
}
