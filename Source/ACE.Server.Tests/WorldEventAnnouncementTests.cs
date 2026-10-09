using System;
using System.IO;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the 2026-08-19 announcement fix: the source-level goalDisplayName/successTemplate/
    /// failTemplate overrides apply ONLY when the composed goal is DestroySource, and goal-owned
    /// successTemplate/failTemplate flavour the outcome line for every goal (TECH-DESIGN 5.4).
    ///
    /// Loaded against the SHIPPED Content/events/axes/{goals,sources}.json, the same pattern as
    /// WorldEventAxisStoreTests, so this exercises the real content an in-game run reads - not a
    /// hand-written probe that could drift from it.
    /// </summary>
    [TestClass]
    public class WorldEventAnnouncementTests
    {
        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "goals.json")))
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
            var bosses = File.ReadAllText(Path.Combine(dir, "bosses.json"));

            return WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors, bosses);
        }

        private static WorldEventComposition Compose(WorldEventAxisStore store, string sourceId, string goalId)
        {
            return new WorldEventComposition(store.Sources[sourceId], null, null, store.Goals[goalId], null,
                null, null, store);
        }

        // ---- GoalDisplayName: DestroySource-only scoping (the reported defect) -------------------------

        [TestMethod]
        public void GoalDisplayName_ElementPortal_KillCount_UsesTheGoalsOwnName_NotThePillarOverride()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "kill_count");

            Assert.AreEqual("Defend Against the Waves", composition.GoalDisplayName);
        }

        [TestMethod]
        public void GoalDisplayName_ElementPortal_KillBoss_UsesTheGoalsOwnName_NotThePillarOverride()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "kill_boss");

            Assert.AreEqual("Slay the Champion", composition.GoalDisplayName);
        }

        [TestMethod]
        public void GoalDisplayName_ElementPortal_DestroySource_UsesThePillarOverride()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "destroy_source");

            Assert.AreEqual("Shatter the Pillars", composition.GoalDisplayName);
        }

        // ---- OutcomeLine (Success): each goal renders its own template, tokens substituted -------------

        [TestMethod]
        public void OutcomeLine_Success_KillCount_RendersTheWavesTemplate()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "kill_count");

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, null, composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            StringAssert.StartsWith(line, "[World Event] The waves at Holtburg have been repelled!");
        }

        [TestMethod]
        public void OutcomeLine_Success_KillBoss_RendersTheBossTemplate()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "kill_boss");

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, "Ashmaw", composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            StringAssert.StartsWith(line, "[World Event] Ashmaw has been slain at Holtburg!");
        }

        [TestMethod]
        public void OutcomeLine_Success_DestroySource_RendersThePillarTemplate()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "destroy_source");

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, null, composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            // The source's own template ("shattered") wins over the destroy_source goal's generic
            // "destroyed" template - both are DestroySource-scoped, and the source is more specific.
            StringAssert.StartsWith(line, "[World Event] The pillars at Holtburg have been shattered!");
        }

        // ---- OutcomeLine (FailedTimeout): same three goals, {reason} substituted -----------------------

        [TestMethod]
        public void OutcomeLine_FailedTimeout_KillCount_RendersTheDefenceTemplate()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "kill_count");

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, null, composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            Assert.AreEqual(
                "[World Event] The defence of Holtburg has failed - time ran out. A consolation cache remains for a short while.",
                line);
        }

        [TestMethod]
        public void OutcomeLine_FailedTimeout_KillBoss_RendersTheBossStandsTemplate()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "kill_boss");

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, "Ashmaw", composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            Assert.AreEqual(
                "[World Event] Ashmaw still stands at Holtburg - time ran out. A consolation cache remains for a short while.",
                line);
        }

        [TestMethod]
        public void OutcomeLine_FailedTimeout_DestroySource_RendersThePillarsStandTemplate()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "element_portal_fire", "destroy_source");

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, null, composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            Assert.AreEqual(
                "[World Event] The pillars at Holtburg still stand - time ran out. A consolation cache remains for a short while.",
                line);
        }

        // ---- rift theme: no source-level templates, the goal's own template still applies --------------

        [TestMethod]
        public void OutcomeLine_Success_Rift_DestroySource_UsesTheGoalsOwnTemplate_WithTheRiftDefaults()
        {
            var store = LoadShippedStore();
            var composition = Compose(store, "rift", "destroy_source");

            Assert.AreEqual("Destroy the Rifts", composition.GoalDisplayName);

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, composition.Goal, "Holtburg",
                WorldEventMvp.None, null, 0, null, composition.GoalDisplayName, composition.SuccessTemplate,
                composition.FailTemplate, composition.ObjectiveNoun, composition.ObjectiveNounPlural);

            StringAssert.StartsWith(line, "[World Event] The rifts at Holtburg have been destroyed!");
        }

        // ---- template absent falls back to the legacy fixed shape ---------------------------------------

        [TestMethod]
        public void OutcomeLine_Success_NoTemplate_FallsBackToTheLegacyFixedShape()
        {
            var goal = new GoalDef { Id = "probe", DisplayName = "Probe Goal", TypeKind = GoalType.KillCount };

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "Holtburg",
                WorldEventMvp.None, null, 0);

            Assert.AreEqual("[World Event] Probe Goal at Holtburg succeeded! Most kills: none (0). 0 defenders took part.", line);
        }

        [TestMethod]
        public void OutcomeLine_FailedTimeout_NoTemplate_FallsBackToTheLegacyFixedShape()
        {
            var goal = new GoalDef { Id = "probe", DisplayName = "Probe Goal", TypeKind = GoalType.KillCount };

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, goal, "Holtburg",
                WorldEventMvp.None, null, 0);

            Assert.AreEqual(
                "[World Event] Probe Goal at Holtburg failed - time ran out. A consolation cache remains for a short while.",
                line);
        }

        // ---- an unrecognised token is left literal -------------------------------------------------------

        [TestMethod]
        public void OutcomeLine_Success_UnknownToken_IsLeftLiteral()
        {
            var goal = new GoalDef
            {
                Id = "probe", DisplayName = "Probe Goal", TypeKind = GoalType.KillCount,
                SuccessTemplate = "{anchor} holds, {notatoken} stands firm."
            };

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "Holtburg",
                WorldEventMvp.None, null, 0, null, null, goal.SuccessTemplate);

            StringAssert.StartsWith(line, "[World Event] Holtburg holds, {notatoken} stands firm.");
        }
    }
}
