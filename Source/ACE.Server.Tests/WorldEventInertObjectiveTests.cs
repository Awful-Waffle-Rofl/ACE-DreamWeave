using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the 2026-08-19 change that retired the destroy_pillars goal into destroy_source:
    /// the source-level flavour overrides (goalDisplayName / objectiveNoun / objectiveNounPlural), and the
    /// InertObjective spawn kind that places a theme's objectives as scenery when the composed goal is not
    /// DestroySource.
    ///
    /// Only pure members are exercised (TECH-DESIGN D6). The placement loop itself needs a live landblock
    /// and a real Creature, so the in-world half goes to Docs/VERIFY-QUEUE.md rather than to CI.
    /// </summary>
    [TestClass]
    public class WorldEventInertObjectiveTests
    {
        // ---- (a) spawn-kind selection ------------------------------------------------------------------

        [TestMethod]
        public void ObjectiveSpawnKind_DestroySourceIsTheOnlyGoalThatFightsTheObjectives()
        {
            Assert.AreEqual(WorldEventSpawnKind.Source,
                WorldEvent.ObjectiveSpawnKind(new GoalDef { Id = "destroy_source", TypeKind = GoalType.DestroySource }));

            Assert.AreEqual(WorldEventSpawnKind.InertObjective,
                WorldEvent.ObjectiveSpawnKind(new GoalDef { Id = "kill_count", TypeKind = GoalType.KillCount }));

            Assert.AreEqual(WorldEventSpawnKind.InertObjective,
                WorldEvent.ObjectiveSpawnKind(new GoalDef { Id = "kill_boss", TypeKind = GoalType.KillBoss }));

            Assert.AreEqual(WorldEventSpawnKind.InertObjective,
                WorldEvent.ObjectiveSpawnKind(new GoalDef { Id = "hold", TypeKind = GoalType.Hold }));
        }

        [TestMethod]
        public void ObjectiveSpawnKind_NullGoalIsInert()
        {
            // A run with no resolved goal must never arm the "all sources dead" wave cut-off, so the safe
            // default is scenery.
            Assert.AreEqual(WorldEventSpawnKind.InertObjective, WorldEvent.ObjectiveSpawnKind(null));
        }

        [TestMethod]
        public void SourcesWereSpawned_OnlyARealSourceCounts()
        {
            Assert.IsTrue(WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.Source));

            Assert.IsFalse(WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.InertObjective));
            Assert.IsFalse(WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.Wave));
            Assert.IsFalse(WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.Boss));
            Assert.IsFalse(WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.Decor));
            Assert.IsFalse(WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.Npc));
        }

        /// <summary>
        /// The reason SourcesWereSpawned matters: an inert objective can never die, so if it were reported
        /// as a spawned source the wave cut-off would be armed against a condition nothing can satisfy.
        /// </summary>
        [TestMethod]
        public void ShouldStopWavesForDeadSources_NeverFiresForAnInertObjectiveRun()
        {
            var sourcesSpawned = WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.InertObjective);

            Assert.IsFalse(WorldEvent.ShouldStopWavesForDeadSources(sourcesSpawned, true, true));

            // The same run under destroy_source does arm it.
            Assert.IsTrue(WorldEvent.ShouldStopWavesForDeadSources(
                WorldEvent.SourcesWereSpawned(WorldEventSpawnKind.Source), true, true));
        }

        // ---- (b) spawn-kind classifier truth table -----------------------------------------------------

        [TestMethod]
        public void InertObjective_IsACreature_ButCountsAsNothing()
        {
            // It is the same objective weenie a Source would be, so it must still be a Creature...
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.InertObjective));

            // ...but it is not trash pressure and not "live" - the CountsAsLive false is what routes it down
            // Adopt's decor branch: no sourceGuids entry, no P_WorldEvent, no throughput credit.
            Assert.IsFalse(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.InertObjective));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.InertObjective));
        }

        [TestMethod]
        public void InertObjective_TakesTheOrdinaryPlacementRecipe()
        {
            // No sky drop, no disc resample, and the ordinary attempt budget - only Decor and Wave differ.
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.InertObjective, 20f));
            Assert.IsFalse(WorldEventSpawner.ResamplesOnRetry(WorldEventSpawnKind.InertObjective, SourceGeometry.Disc));

            Assert.AreEqual(
                WorldEventSpawner.PlacementAttemptsFor(WorldEventSpawnKind.Source, SourceGeometry.Disc),
                WorldEventSpawner.PlacementAttemptsFor(WorldEventSpawnKind.InertObjective, SourceGeometry.Disc));
        }

        [TestMethod]
        public void AddingInertObjective_DidNotChangeAnyOtherKind()
        {
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Wave));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Source));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Boss));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Decor));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Npc));
        }

        // ---- (c) DestroySourceObjective nouns ----------------------------------------------------------

        private static GoalDef DestroySourceGoal(string progressTemplate = "{remaining} {nounPlural} remain.")
        {
            return new GoalDef
            {
                Id = "destroy_source",
                DisplayName = "Destroy the Source",
                TypeKind = GoalType.DestroySource,
                ProgressTemplate = progressTemplate
            };
        }

        private static Func<IReadOnlyCollection<uint>> Guids(params uint[] guids)
        {
            var list = guids.ToList();
            return () => list;
        }

        [TestMethod]
        public void ProgressText_RendersTheSourceNounPlural()
        {
            var objective = new DestroySourceObjective(DestroySourceGoal(), Guids(10, 11), "pillar", "pillars");

            Assert.AreEqual("2 pillars remain.", objective.ProgressText);

            objective.OnDeathCore(10, "Killer", "Killer");

            Assert.AreEqual("1 pillars remain.", objective.ProgressText);
        }

        [TestMethod]
        public void ProgressText_DefaultsToRiftWhenTheSourceDeclaresNoNouns()
        {
            var objective = new DestroySourceObjective(DestroySourceGoal(), Guids(10, 11), null, null);

            Assert.AreEqual("2 rifts remain.", objective.ProgressText);
        }

        [TestMethod]
        public void ProgressText_TwoArgConstructor_KeepsThePreOverrideWording()
        {
            var objective = new DestroySourceObjective(DestroySourceGoal(), Guids(10, 11));

            Assert.AreEqual("2 rifts remain.", objective.ProgressText);
        }

        [TestMethod]
        public void ProgressText_NullTemplateFallback_AlsoUsesTheSourceNoun()
        {
            var pillars = new DestroySourceObjective(DestroySourceGoal(null), Guids(10, 11), "pillar", "pillars");
            Assert.AreEqual("2 of 2 pillars remain.", pillars.ProgressText);

            var rifts = new DestroySourceObjective(DestroySourceGoal(null), Guids(10, 11), null, null);
            Assert.AreEqual("2 of 2 rifts remain.", rifts.ProgressText);
        }

        [TestMethod]
        public void ProgressText_NothingPlaced_UsesTheSourceNoun()
        {
            var pillars = new DestroySourceObjective(DestroySourceGoal(), Guids(), "pillar", "pillars");
            Assert.AreEqual("no pillars were placed", pillars.ProgressText);

            var rifts = new DestroySourceObjective(DestroySourceGoal(), Guids(), null, null);
            Assert.AreEqual("no rifts were placed", rifts.ProgressText);
        }

        [TestMethod]
        public void ProgressText_SingularNounTokenIsAlsoSubstituted()
        {
            var objective = new DestroySourceObjective(
                DestroySourceGoal("{remaining} of {total} - the last {noun} still stands."), Guids(10), "pillar", "pillars");

            Assert.AreEqual("1 of 1 - the last pillar still stands.", objective.ProgressText);
        }

        [TestMethod]
        public void Mvp_NamesTheSourceNoun()
        {
            var pillars = new DestroySourceObjective(DestroySourceGoal(), Guids(10), "pillar", "pillars");
            pillars.OnDeathCore(10, "Killer", "Killer");

            Assert.AreEqual("Killer", pillars.Mvp().Name);
            Assert.AreEqual("destroyed the last pillar", pillars.Mvp().Reason);

            var rifts = new DestroySourceObjective(DestroySourceGoal(), Guids(10), null, null);
            rifts.OnDeathCore(10, "Killer", "Killer");

            Assert.AreEqual("destroyed the last rift", rifts.Mvp().Reason);
        }

        [TestMethod]
        public void Mvp_ComboReason_KeepsTheTopDamagerPlaceholder()
        {
            var objective = new DestroySourceObjective(DestroySourceGoal(), Guids(10), "pillar", "pillars");
            objective.OnDeathCore(10, "Killer", "Bruiser");

            Assert.AreEqual("destroyed the last pillar, top damage: Bruiser", objective.Mvp().Reason);
        }

        // ---- (d) announcer goal-name override ----------------------------------------------------------

        private static GoalDef AnnouncerGoal() => new GoalDef
        {
            Id = "destroy_source", DisplayName = "Destroy the Source", TypeKind = GoalType.DestroySource
        };

        [TestMethod]
        public void GoalName_PrefersTheSourceOverride()
        {
            Assert.AreEqual("Shatter the Pillars", WorldEventAnnouncer.GoalName(AnnouncerGoal(), "Shatter the Pillars"));
        }

        [TestMethod]
        public void GoalName_FallsBackToTheGoalsOwnName()
        {
            Assert.AreEqual("Destroy the Source", WorldEventAnnouncer.GoalName(AnnouncerGoal(), null));
            Assert.AreEqual("Destroy the Source", WorldEventAnnouncer.GoalName(AnnouncerGoal(), ""));
            Assert.AreEqual("Destroy the Source", WorldEventAnnouncer.GoalName(AnnouncerGoal(), "   "));
        }

        [TestMethod]
        public void ActiveLine_UsesTheOverrideWhenTheSourceSetsOne()
        {
            var family = new FamilyDef { Id = "emberwrought", DisplayName = "the Emberwrought" };

            Assert.AreEqual("[World Event] Shatter the Pillars at the gates of Holtburg - the Emberwrought pour in!",
                WorldEventAnnouncer.ActiveLine(null, family.DisplayName, AnnouncerGoal(), "the gates of Holtburg", "Shatter the Pillars"));

            Assert.AreEqual("[World Event] Destroy the Source at the gates of Holtburg - the Emberwrought pour in!",
                WorldEventAnnouncer.ActiveLine(null, family.DisplayName, AnnouncerGoal(), "the gates of Holtburg"));
        }

        [TestMethod]
        public void OutcomeLine_EveryGoalNamingCase_HonoursTheOverride()
        {
            var goal = AnnouncerGoal();

            StringAssert.StartsWith(
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "the anchor", WorldEventMvp.None,
                    null, 3, null, "Shatter the Pillars"),
                "[World Event] Shatter the Pillars at the anchor succeeded!");

            StringAssert.StartsWith(
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.SuccessBossAbsent, goal, "the anchor", WorldEventMvp.None,
                    null, 3, "Ashmaw", "Shatter the Pillars"),
                "[World Event] Shatter the Pillars at the anchor ended: Ashmaw never showed");

            StringAssert.StartsWith(
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, goal, "the anchor", WorldEventMvp.None,
                    null, 3, null, "Shatter the Pillars"),
                "[World Event] Shatter the Pillars at the anchor failed - time ran out.");

            // ...and without an override the goal's own name still stands, unchanged.
            StringAssert.StartsWith(
                WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedWipe, goal, "the anchor", WorldEventMvp.None,
                    null, 3),
                "[World Event] Destroy the Source at the anchor failed - the defenders were wiped out.");
        }

        // ---- (e) composition accessors -----------------------------------------------------------------

        [TestMethod]
        public void Composition_ResolvesTheGoalNameAndNounsFromTheSource()
        {
            var portal = new SourceThemeDef
            {
                Id = "element_portal_fire",
                GoalDisplayName = "Shatter the Pillars",
                ObjectiveNoun = "pillar",
                ObjectiveNounPlural = "pillars"
            };

            var plain = new SourceThemeDef { Id = "ambush" };
            var goal = AnnouncerGoal();

            var withOverride = new WorldEventComposition(portal, null, null, goal, null, null, null, null);
            Assert.AreEqual("Shatter the Pillars", withOverride.GoalDisplayName);
            Assert.AreEqual("pillar", withOverride.ObjectiveNoun);
            Assert.AreEqual("pillars", withOverride.ObjectiveNounPlural);

            var withoutOverride = new WorldEventComposition(plain, null, null, goal, null, null, null, null);
            Assert.AreEqual("Destroy the Source", withoutOverride.GoalDisplayName);
            Assert.AreEqual("rift", withoutOverride.ObjectiveNoun);
            Assert.AreEqual("rifts", withoutOverride.ObjectiveNounPlural);
        }

        // ---- (f) axis-store validation of the new fields -----------------------------------------------

        private const string ValidationFamilies =
            @"{ ""families"": [ { ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"" } ] }";

        private const string ValidationGoals =
            @"{ ""goals"": [ { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
                ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" } ] }";

        private static string SourceJson(string extraFields)
        {
            return @"{ ""sources"": [ {
                ""id"": ""probe"", ""displayName"": ""Probe"", ""geometry"": ""single"",
                ""geometryRadius"": 20.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 40.0,
                ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
                ""rewardRadius"": 60.0, ""compatibleGoals"": [ ""kill_count"" ],
                ""startFlavour"": ""x"", ""waveFlavour"": ""y""" + extraFields + " } ] }";
        }

        private static SourceThemeDef ParseProbe(string extraFields, out WorldEventAxisStore store)
        {
            store = WorldEventAxisStore.Parse(SourceJson(extraFields), ValidationFamilies, ValidationGoals, null, null);
            return store.Sources["probe"];
        }

        [TestMethod]
        public void AxisStore_AbsentOverrides_TakeTheDefaultsSilently()
        {
            var probe = ParseProbe("", out var store);

            Assert.IsNull(probe.GoalDisplayName);
            Assert.AreEqual("rift", probe.ObjectiveNoun);
            Assert.AreEqual("rifts", probe.ObjectiveNounPlural);

            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("goalDisplayName") || d.Contains("objectiveNoun")),
                "an absent key is not a diagnostic");
        }

        [TestMethod]
        public void AxisStore_DeclaredOverrides_RoundTrip()
        {
            var probe = ParseProbe(
                @", ""goalDisplayName"": ""Shatter the Pillars"", ""objectiveNoun"": ""pillar"", ""objectiveNounPlural"": ""pillars""",
                out var store);

            Assert.AreEqual("Shatter the Pillars", probe.GoalDisplayName);
            Assert.AreEqual("pillar", probe.ObjectiveNoun);
            Assert.AreEqual("pillars", probe.ObjectiveNounPlural);
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void AxisStore_BlankOverrides_AreRepairedWithADiagnostic_AndNeverDropTheTheme()
        {
            var probe = ParseProbe(
                @", ""goalDisplayName"": ""   "", ""objectiveNoun"": """", ""objectiveNounPlural"": """"",
                out var store);

            Assert.IsNotNull(probe, "a blank override must never drop the theme");
            Assert.IsNull(probe.GoalDisplayName);
            Assert.AreEqual("rift", probe.ObjectiveNoun);
            Assert.AreEqual("rifts", probe.ObjectiveNounPlural);

            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("blank goalDisplayName")), string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("blank objectiveNoun,")), string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("blank objectiveNounPlural")), string.Join(" | ", store.Diagnostics));
        }

        // ---- (g) composer: one element portal, three goals ----------------------------------------------

        private const string ComposerSources = @"{ ""sources"": [ {
            ""id"": ""element_portal_fire"", ""displayName"": ""Fire Portal"", ""geometry"": ""disc"",
            ""geometryRadius"": 10.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0,
            ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
            ""rewardRadius"": 60.0,
            ""objectiveHealth"": { ""base"": 8000, ""perParticipant"": 2500, ""cap"": 80000 },
            ""objectives"": [
                { ""wcid"": 1002660, ""dx"": 6.5, ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 0.0 },
                { ""wcid"": 1002660, ""dx"": -6.5, ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 0.0 } ],
            ""compatibleGoals"": [ ""destroy_source"", ""kill_count"", ""kill_boss"" ],
            ""goalDisplayName"": ""Shatter the Pillars"",
            ""objectiveNoun"": ""pillar"", ""objectiveNounPlural"": ""pillars"",
            ""startFlavour"": ""x"", ""waveFlavour"": ""y"" }, {
            ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""edges"",
            ""geometryRadius"": 45.0, ""geometryPoints"": 3, ""waveIntervalSeconds"": 45.0,
            ""maxAlive"": 24, ""waveCount"": { ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 },
            ""rewardRadius"": 60.0, ""compatibleGoals"": [ ""kill_count"" ],
            ""startFlavour"": ""x"", ""waveFlavour"": ""y"" } ] }";

        private const string ComposerGoals = @"{ ""goals"": [
            { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
              ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
              ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" },
            { ""id"": ""destroy_source"", ""displayName"": ""Destroy the Source"", ""type"": ""DestroySource"",
              ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"",
              ""progressTemplate"": ""{remaining} {nounPlural} remain."" },
            { ""id"": ""kill_boss"", ""displayName"": ""Slay the Champion"", ""type"": ""KillBoss"",
              ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"",
              ""progressTemplate"": ""The champion still stands."" } ] }";

        private const string ComposerRewards = @"{ ""rewards"": [ {
            ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
            ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
            ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

        private const string ComposerBosses =
            @"{ ""bosses"": [ { ""id"": ""ember_boss"", ""displayName"": ""Ember Boss"", ""wcid"": 1002619 } ] }";

        private static WorldEventAxisStore ComposerStore()
        {
            return WorldEventAxisStore.Parse(ComposerSources, ValidationFamilies, ComposerGoals, ComposerRewards,
                null, ComposerBosses);
        }

        private static WorldEventCatalog ComposerCatalog()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1002604,
                ClassName = "testcreature1002604",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    [PropertyBool.WorldEventCreature] = true,
                    [PropertyBool.Attackable] = true
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.Level] = 20,
                    [PropertyInt.WorldEventRole] = 0
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.WorldEventFamily] = "emberwrought",
                    [PropertyString.Name] = "Test Creature"
                }
            };

            return WorldEventCatalogBuilder.Build(new List<Weenie> { weenie }, out _);
        }

        private static WorldEventRequest PortalRequest(string goalId, string bossId = null)
        {
            return new WorldEventRequest
            {
                SourceId = "element_portal_fire",
                FamilyId = "emberwrought",
                GoalId = goalId,
                BossId = bossId,
                AnchorPosition = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test"
            };
        }

        [TestMethod]
        public void Compose_ElementPortal_AcceptsDestroySource_AndSpawnsItsObjectivesAsSources()
        {
            Assert.IsTrue(WorldEventComposer.TryCompose(ComposerStore(), ComposerCatalog(),
                PortalRequest("destroy_source"), out var composition, out var error), error);

            Assert.AreEqual(WorldEventSpawnKind.Source, WorldEvent.ObjectiveSpawnKind(composition.Goal));
            Assert.AreEqual("Shatter the Pillars", composition.GoalDisplayName);
            Assert.AreEqual("pillars", composition.ObjectiveNounPlural);
        }

        [TestMethod]
        public void Compose_ElementPortal_AcceptsKillCount_AndTheObjectivesBecomeInertScenery()
        {
            Assert.IsTrue(WorldEventComposer.TryCompose(ComposerStore(), ComposerCatalog(),
                PortalRequest("kill_count"), out var composition, out var error), error);

            Assert.AreEqual(WorldEventSpawnKind.InertObjective, WorldEvent.ObjectiveSpawnKind(composition.Goal));
            Assert.IsFalse(WorldEvent.SourcesWereSpawned(WorldEvent.ObjectiveSpawnKind(composition.Goal)));

            // The pillars are still PLACED - they are what makes the portal read as a portal.
            Assert.AreEqual(2, composition.Source.Objectives.Count);
        }

        [TestMethod]
        public void Compose_ElementPortal_AcceptsKillBoss_WithBossAuto()
        {
            Assert.IsTrue(WorldEventComposer.TryCompose(ComposerStore(), ComposerCatalog(),
                PortalRequest("kill_boss", WorldEventComposer.AutoBossId), out var composition, out var error), error);

            Assert.AreEqual("ember_boss", composition.Boss.Id);
            Assert.AreEqual(BossKind.Named, composition.Boss.Kind);
            Assert.AreEqual(WorldEventSpawnKind.InertObjective, WorldEvent.ObjectiveSpawnKind(composition.Goal));
        }

        [TestMethod]
        public void Compose_ElementPortal_KillBossWithoutABossIsStillRefused()
        {
            Assert.IsFalse(WorldEventComposer.TryCompose(ComposerStore(), ComposerCatalog(),
                PortalRequest("kill_boss"), out _, out var error));

            Assert.AreEqual("goal 'kill_boss' needs --boss (family_champion, auto, or a named boss id)", error);
        }

        [TestMethod]
        public void Compose_IncompatiblePairIsStillRefused()
        {
            var request = PortalRequest("kill_count");
            request.SourceId = "ambush";
            request.GoalId = "destroy_source";

            Assert.IsFalse(WorldEventComposer.TryCompose(ComposerStore(), ComposerCatalog(), request, out _, out var error));

            Assert.AreEqual("goal 'destroy_source' is not compatible with source 'ambush' (compatible: kill_count)", error);
        }

        [TestMethod]
        public void Compose_RetiredGoalIdIsUnknown()
        {
            var request = PortalRequest("destroy_pillars");

            Assert.IsFalse(WorldEventComposer.TryCompose(ComposerStore(), ComposerCatalog(), request, out _, out var error));

            StringAssert.StartsWith(error, "unknown goal 'destroy_pillars'");
        }
    }
}
