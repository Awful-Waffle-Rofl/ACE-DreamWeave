using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A RANDOM World Event start walks the composer's weighted candidate order: when the goal's start-spot rule (or the
    /// objective factory) refuses one candidate, WorldEventManager.TryStart tries the next, up to
    /// WorldEventManager.MaxRandomStartCandidates. A pinned start stays one candidate.
    ///
    /// Runs the REAL TryStart against a fixture store published through InitializeWith (five kill_count sources and two
    /// families, so ten candidates compose). The start-spot rule is replaced through
    /// WorldEventObjectiveFactory.StartSpotRuleOverride. Every run uses a positive teaser, so it stays in Idle and no
    /// test ticks. Every seam is restored in a cleanup.
    /// </summary>
    [TestClass]
    public class WorldEventRandomStartFallbackTests
    {
        private const string EnabledKey = "world_events_enabled";
        private const uint ZaikhalCell = 0x8090000C;

        private static readonly string[] SourceIds = { "src_a", "src_b", "src_c", "src_d", "src_e" };

        private const string SourceTemplate = @"{{
            ""id"": ""{0}"", ""displayName"": ""{0}"", ""geometry"": ""disc"",
            ""geometryRadius"": 10.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0,
            ""maxAlive"": 20, ""waveCount"": {{ ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 }},
            ""rewardRadius"": 60.0, ""compatibleGoals"": [ ""kill_count"" ],
            ""startFlavour"": ""x near {{anchor}}"", ""waveFlavour"": ""y"" }}";

        private static string Sources() =>
            "{ \"sources\": [ " + string.Join(", ", SourceIds.Select(id => string.Format(SourceTemplate, id))) + " ] }";

        private const string Families =
            @"{ ""families"": [ { ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"" }, " +
            @"{ ""id"": ""frostbound"", ""displayName"": ""the Frostbound"" } ] }";

        private const string Goals = @"{ ""goals"": [
            { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
              ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
              ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" } ] }";

        private const string Rewards = @"{ ""rewards"": [ {
            ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
            ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
            ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

        private const string Bosses =
            @"{ ""bosses"": [ { ""id"": ""ember_boss"", ""displayName"": ""Ember Boss"", ""wcid"": 1002619 } ] }";

        private static WorldEventAxisStore Store() =>
            WorldEventAxisStore.Parse(Sources(), Families, Goals, Rewards, null, Bosses);

        private static Weenie Member(uint wcid, string family, int level, bool caster)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "testcreature" + wcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    [PropertyBool.WorldEventCreature] = true,
                    [PropertyBool.Attackable] = true
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.Level] = level,
                    [PropertyInt.WorldEventRole] = 0
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.WorldEventFamily] = family,
                    [PropertyString.Name] = "Test Creature " + wcid
                }
            };

            if (caster)
                weenie.PropertiesSpellBook = new Dictionary<int, float> { [157] = 1.0f };

            return weenie;
        }

        private static WorldEventCatalog Catalog() => WorldEventCatalogBuilder.Build(new List<Weenie>
        {
            Member(1002604, "emberwrought", 20, true),
            Member(1002605, "frostbound", 20, true)
        }, out _);

        private static Position Asked() => new Position(ZaikhalCell, 96, 96, 124, 0, 0, 0, 1, 0);

        private static WorldEventRequest RandomRequest(bool dryRun = false) => new WorldEventRequest
        {
            Random = true,
            AnchorPosition = Asked(),
            TeaserLeadSeconds = 60,
            DryRun = dryRun
        };

        // ---- harness ----

        private WorldEventAxisStore savedStore;
        private WorldEventCatalog savedCatalog;
        private Action<string> savedBroadcast;
        private Action<string> savedRelay;
        private Func<GoalDef, IWorldEventStartSpotRule> savedOverride;
        private Func<WorldEvent, string> savedCreateRefusal;
        private Func<Random> savedRng;

        [TestInitialize]
        public void Setup()
        {
            savedStore = WorldEventManager.Store;
            savedCatalog = WorldEventManager.Catalog;
            savedBroadcast = WorldEventAnnouncer.BroadcastSink;
            savedRelay = WorldEventAnnouncer.DiscordRelaySink;
            savedOverride = WorldEventObjectiveFactory.StartSpotRuleOverride;
            savedCreateRefusal = WorldEventObjectiveFactory.CreateRefusalOverride;
            savedRng = WorldEventManager.RandomStartRngOverride;

            WorldEventAnnouncer.BroadcastSink = _ => { };
            WorldEventAnnouncer.DiscordRelaySink = _ => { };

            WorldEventManager.InitializeWith(() => (Store(), Catalog()));
            PropertyManager.ModifyBool(EnabledKey, true);
        }

        [TestCleanup]
        public void Teardown()
        {
            try
            {
                WorldEventObjectiveFactory.StartSpotRuleOverride = savedOverride;
                WorldEventObjectiveFactory.CreateRefusalOverride = savedCreateRefusal;
                WorldEventManager.RandomStartRngOverride = savedRng;
                WorldEventManager.StopCurrent(WorldEventOutcome.AbortedAdmin, "test cleanup");
            }
            finally
            {
                PropertyManager.ModifyBool(EnabledKey, false);
                WorldEventManager.InitializeWith(() => (savedStore, savedCatalog));
                WorldEventAnnouncer.BroadcastSink = savedBroadcast;
                WorldEventAnnouncer.DiscordRelaySink = savedRelay;
            }
        }

        /// <summary>
        /// A start-spot rule that records every composition it is asked about and refuses the first
        /// <see cref="refuseFirst"/> of them. When <see cref="moveBeforeRefusing"/> is set it first moves the anchor the
        /// way the Hold rule does (WorldEventHoldStartSpotRule.AnchorAt, the in-place write).
        /// </summary>
        private sealed class CountingRule : IWorldEventStartSpotRule
        {
            public const string Error = "no open ground near the anchor (test rule)";

            private readonly int refuseFirst;
            private readonly bool moveBeforeRefusing;

            public readonly List<WorldEventComposition> Asked = new List<WorldEventComposition>();

            /// <summary>Each asked composition's anchor as it arrived, before this rule touched it.</summary>
            public readonly List<(uint cell, float x, float y, float z)> ArrivedAt = new List<(uint, float, float, float)>();

            public CountingRule(int refuseFirst, bool moveBeforeRefusing = false)
            {
                this.refuseFirst = refuseFirst;
                this.moveBeforeRefusing = moveBeforeRefusing;
            }

            public bool TryResolve(WorldEventComposition composition, WorldEventRequest request, out string error)
            {
                error = null;

                Asked.Add(composition);

                var a = composition.AnchorPosition;
                ArrivedAt.Add((a.Cell, a.PositionX, a.PositionY, a.PositionZ));

                if (Asked.Count > refuseFirst)
                    return true;

                if (moveBeforeRefusing)
                {
                    var g = a.ToGlobal();
                    Assert.IsTrue(WorldEventHoldStartSpotRule.AnchorAt(a, g.X + 300, g.Y + 300, 50f), "the test move must apply");
                }

                error = Error;
                return false;
            }
        }

        private static void AssertAt(Position expected, (uint cell, float x, float y, float z) actual, string what)
        {
            Assert.AreEqual(expected.Cell, actual.cell, what + ": cell");
            Assert.AreEqual(expected.PositionX, actual.x, 0.001f, what + ": x");
            Assert.AreEqual(expected.PositionY, actual.y, 0.001f, what + ": y");
            Assert.AreEqual(expected.PositionZ, actual.z, 0.001f, what + ": z");
        }

        private static (uint, float, float, float) At(Position p) => (p.Cell, p.PositionX, p.PositionY, p.PositionZ);

        [TestMethod]
        public void RandomStart_FirstCandidateRefused_TheSecondStarts()
        {
            var rule = new CountingRule(refuseFirst: 1);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            WorldEvent evt;
            string error;
            WorldEventStartRefusal refusal;
            List<string> lines;

            using (var capture = new MarketLogCapture())
            {
                Assert.IsTrue(WorldEventManager.TryStart(RandomRequest(), out evt, out error, out refusal), error);
                lines = capture.Events.Select(e => e.RenderedMessage).ToList();
            }

            var expectedLine = $"[WORLDEVENT] random candidate 1 source={rule.Asked[0].Source.Id} goal=kill_count refused: {CountingRule.Error}";
            CollectionAssert.Contains(lines, expectedLine, "the refused candidate is logged once, in the documented shape");
            Assert.AreEqual(1, lines.Count(l => l.StartsWith("[WORLDEVENT] random candidate ")), "only the refused candidate is logged");

            Assert.AreEqual(WorldEventStartRefusal.None, refusal);
            Assert.AreEqual(2, rule.Asked.Count, "the refused first candidate is followed by exactly one more");
            Assert.AreNotSame(rule.Asked[0], rule.Asked[1], "the second candidate is a fresh composition");
            Assert.AreSame(rule.Asked[1], evt.Composition, "the run started is the second candidate");
            Assert.AreSame(evt, WorldEventManager.Current);
        }

        [TestMethod]
        public void RandomStart_EveryCandidateRefused_StopsAtTheCap_ReturnsTheLastRefusal_RegistersNothing()
        {
            var rule = new CountingRule(refuseFirst: int.MaxValue);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            // Control: the fixture really composes more candidates than the cap, so stopping at the cap is the cap's doing.
            var composable = WorldEventComposer.RandomCompositions(Store(), Catalog(), RandomRequest(), new Random(1))
                .Count(s => s.Composition != null);
            Assert.IsTrue(composable > WorldEventManager.MaxRandomStartCandidates,
                $"fixture composes {composable} candidates; it must exceed {WorldEventManager.MaxRandomStartCandidates}");

            Assert.IsFalse(WorldEventManager.TryStart(RandomRequest(), out var evt, out var error, out var refusal));

            Assert.AreEqual(WorldEventManager.MaxRandomStartCandidates, rule.Asked.Count, "tries exactly the cap");
            Assert.AreEqual(WorldEventStartRefusal.CompositionFailed, refusal);
            Assert.AreEqual(CountingRule.Error, error, "the last candidate's own refusal, not a generic compose error");
            Assert.IsNull(evt);
            Assert.IsNull(WorldEventManager.Current, "nothing is registered");
        }

        [TestMethod]
        public void RandomStart_RefusedCandidateMovedTheAnchor_RequestAndNextCandidateStartFromTheAskedSpot()
        {
            var rule = new CountingRule(refuseFirst: 1, moveBeforeRefusing: true);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            var request = RandomRequest();
            var asked = Asked();

            Assert.IsTrue(WorldEventManager.TryStart(request, out var evt, out var error, out _), error);

            // The first candidate really was moved (else this test proves nothing).
            Assert.AreNotEqual(asked.Cell, rule.Asked[0].AnchorPosition.Cell, "the refused candidate's anchor was moved");

            AssertAt(asked, At(request.AnchorPosition), "the request's anchor after a refused candidate");
            AssertAt(asked, rule.ArrivedAt[1], "the second candidate arrived at the asked spot");
            AssertAt(asked, At(evt.Composition.AnchorPosition), "the started run's anchor");
        }

        [TestMethod]
        public void RandomDryRun_FirstCandidateRefused_SucceedsOnTheSecond_BuildsNoRun()
        {
            var rule = new CountingRule(refuseFirst: 1);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            Assert.IsTrue(WorldEventManager.TryStart(RandomRequest(dryRun: true), out var evt, out var error, out _), error);

            Assert.AreEqual(2, rule.Asked.Count);
            Assert.IsNull(evt);
            Assert.IsNull(WorldEventManager.Current);
        }

        [TestMethod]
        public void PinnedStart_Refused_IsSingleShot()
        {
            var rule = new CountingRule(refuseFirst: int.MaxValue);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            var request = new WorldEventRequest
            {
                SourceId = "src_a", FamilyId = "emberwrought", GoalId = "kill_count",
                AnchorPosition = Asked(), TeaserLeadSeconds = 60
            };

            Assert.IsFalse(WorldEventManager.TryStart(request, out var evt, out var error, out var refusal));

            Assert.AreEqual(1, rule.Asked.Count, "a pinned start is asked once");
            Assert.AreEqual(WorldEventStartRefusal.CompositionFailed, refusal);
            Assert.AreEqual(CountingRule.Error, error);
            Assert.IsNull(evt);
            Assert.IsNull(WorldEventManager.Current);

            // Control: the same pinned request starts once the rule stands aside.
            WorldEventObjectiveFactory.StartSpotRuleOverride = savedOverride;
            Assert.IsTrue(WorldEventManager.TryStart(request, out _, out var okError, out _), okError);
        }

        // ---- objective-factory refusal on the fallback path ----

        [TestMethod]
        public void RandomStart_ObjectiveFactoryRefusesTheFirstBuiltRun_TheNextStarts_TheDiscardedRunIsNowhere()
        {
            const string objectiveError = "objective refused (test seam)";
            var builtIds = new List<uint>();

            WorldEventObjectiveFactory.CreateRefusalOverride = built =>
            {
                builtIds.Add(built.RunId);
                return builtIds.Count == 1 ? objectiveError : null;
            };

            WorldEvent evt;
            string error;
            List<string> lines;

            using (var capture = new MarketLogCapture())
            {
                Assert.IsTrue(WorldEventManager.TryStart(RandomRequest(), out evt, out error, out _), error);
                lines = capture.Events.Select(e => e.RenderedMessage).ToList();
            }

            Assert.AreEqual(2, builtIds.Count, "the first built run was discarded and a second was built");
            Assert.AreEqual(builtIds[1], evt.RunId, "the started run is the second one built");
            Assert.IsTrue(builtIds[1] > builtIds[0], "the discarded run consumed its own id");
            Assert.AreSame(evt, WorldEventManager.Current);
            Assert.IsNull(WorldEventManager.FindRun(builtIds[0]), "the discarded run is not findable");
            Assert.IsTrue(lines.Any(l => l.StartsWith("[WORLDEVENT] random candidate 1 ") && l.EndsWith("refused: " + objectiveError)),
                "the objective refusal is logged like any other candidate refusal");
        }

        // ---- composer parity and a short sequence ----

        [TestMethod]
        public void RandomCompositions_First_MatchesTryCompose_ForTheSameSeed()
        {
            var store = Store();
            var catalog = Catalog();

            for (var seed = 0; seed < 12; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var viaTry, out var error, new Random(seed)), error);

                var first = WorldEventComposer.RandomCompositions(store, catalog, RandomRequest(), new Random(seed)).First();

                Assert.IsNotNull(first.Composition, $"seed {seed}: {first.Error}");
                Assert.AreEqual(viaTry.Source.Id, first.Composition.Source.Id, $"seed {seed}: source");
                Assert.AreEqual(viaTry.Goal.Id, first.Composition.Goal.Id, $"seed {seed}: goal");
                Assert.AreEqual(viaTry.Boss.Id, first.Composition.Boss.Id, $"seed {seed}: boss");
                CollectionAssert.AreEqual(viaTry.Families.Select(f => f.Id).ToList(),
                    first.Composition.Families.Select(f => f.Id).ToList(), $"seed {seed}: families");
            }
        }

        [TestMethod]
        public void RandomStart_SequenceEndsBeforeTheCap_AllRefused_ReturnsTheLastCandidatesRefusal()
        {
            WorldEventRequest Restricted() => new WorldEventRequest
            {
                Random = true,
                FamilyIds = new List<string> { "emberwrought" },
                AllowedSourceIds = new List<string> { "src_a", "src_b", "src_c" },
                AnchorPosition = Asked(),
                TeaserLeadSeconds = 60
            };

            // Control: exactly three candidates compose, fewer than the cap.
            var composable = WorldEventComposer.RandomCompositions(Store(), Catalog(), Restricted(), new Random(3))
                .Count(s => s.Composition != null);
            Assert.AreEqual(3, composable);

            var rule = new CountingRule(refuseFirst: int.MaxValue);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            Assert.IsFalse(WorldEventManager.TryStart(Restricted(), out var evt, out var error, out var refusal));

            Assert.AreEqual(3, rule.Asked.Count);
            Assert.AreEqual(WorldEventStartRefusal.CompositionFailed, refusal);
            Assert.AreEqual(CountingRule.Error, error, "the last candidate's refusal, not the exhausted search's compose error");
            Assert.IsNull(evt);
            Assert.IsNull(WorldEventManager.Current);
        }

        // ---- the anchor copy is exact ----

        [TestMethod]
        public void RandomStart_HandBuiltAnchorCell_IsKeptVerbatim()
        {
            // relativePos: true keeps cell 0x000C although (96, 96) lies in cell 0x0025: a copy that re-derives the cell
            // from the offsets would change it.
            var request = RandomRequest();
            request.AnchorPosition = new Position(ZaikhalCell, 96, 96, 124, 0, 0, 0, 1, 0, relativePos: true);

            Assert.AreEqual(ZaikhalCell, request.AnchorPosition.Cell, "fixture: the hand-built cell stands");

            Assert.IsTrue(WorldEventManager.TryStart(request, out var evt, out var error, out _), error);

            Assert.AreEqual(ZaikhalCell, evt.Composition.AnchorPosition.Cell, "the run's anchor keeps the asked cell");
            Assert.AreEqual(ZaikhalCell, request.AnchorPosition.Cell, "the request is untouched");
            Assert.AreNotSame(request.AnchorPosition, evt.Composition.AnchorPosition, "the run holds a copy");
            Assert.AreEqual(96f, evt.Composition.AnchorPosition.PositionX, 0.001f);
            Assert.AreEqual(96f, evt.Composition.AnchorPosition.PositionY, 0.001f);
            Assert.AreEqual(124f, evt.Composition.AnchorPosition.PositionZ, 0.001f);
        }

        // ---- duration refusals fall back on a random start ----

        private const string DurationSources = @"{ ""sources"": [ {
            ""id"": ""src_a"", ""displayName"": ""src_a"", ""geometry"": ""disc"",
            ""geometryRadius"": 10.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0,
            ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
            ""rewardRadius"": 60.0, ""compatibleGoals"": [ {0} ],
            ""startFlavour"": ""x near {anchor}"", ""waveFlavour"": ""y"" } ] }";

        private static string Goal(string id, int maxSeconds) => $@"{{ ""id"": ""{id}"", ""displayName"": ""{id}"", ""type"": ""KillCount"",
              ""count"": {{ ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }}, ""holdSeconds"": 0,
              ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{{killed}} of {{target}} slain."", ""maxDurationSeconds"": {maxSeconds} }}";

        /// <summary>One source; goal "short" runs at most 600 s, goal "long" (when included) 900 s.</summary>
        private static WorldEventAxisStore DurationStore(bool withLong)
        {
            var goalIds = withLong ? "\"short\", \"long\"" : "\"short\"";
            var goals = withLong ? Goal("short", 600) + ", " + Goal("long", 900) : Goal("short", 600);

            return WorldEventAxisStore.Parse(DurationSources.Replace("{0}", goalIds), Families,
                "{ \"goals\": [ " + goals + " ] }", Rewards, null, Bosses);
        }

        private static WorldEventRequest LongMinimum(bool random = true) => new WorldEventRequest
        {
            Random = random,
            SourceId = random ? null : "src_a",
            FamilyId = random ? null : "emberwrought",
            GoalId = random ? null : "short",
            AnchorPosition = Asked(),
            TeaserLeadSeconds = 60,
            MinDurationSeconds = 700
        };

        /// <summary>A seed whose weighted order puts the 600 s goal first, found against the same store the manager holds.</summary>
        private static int SeedWithShortFirst(WorldEventAxisStore store)
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var first = WorldEventComposer.RandomCompositions(store, Catalog(), LongMinimum(), new Random(seed)).First();

                if (first.Composition?.Goal?.Id == "short")
                    return seed;
            }

            Assert.Fail("no seed in 0..199 orders the 600 s goal first");
            return -1;
        }

        [TestMethod]
        public void RandomStart_FirstCandidatesGoalTooShortForTheMinimum_StartsOnTheLongerGoal()
        {
            WorldEventManager.InitializeWith(() => (DurationStore(withLong: true), Catalog()));

            var seed = SeedWithShortFirst(DurationStore(withLong: true));
            WorldEventManager.RandomStartRngOverride = () => new Random(seed);

            WorldEvent evt;
            string error;
            List<string> lines;

            using (var capture = new MarketLogCapture())
            {
                Assert.IsTrue(WorldEventManager.TryStart(LongMinimum(), out evt, out error, out _), error);
                lines = capture.Events.Select(e => e.RenderedMessage).ToList();
            }

            Assert.AreEqual("long", evt.Composition.Goal.Id, "the 600 s goal cannot hold a 700 s minimum; the 900 s one can");
            Assert.IsTrue(lines.Any(l => l.StartsWith("[WORLDEVENT] random candidate 1 source=src_a goal=short refused: ")),
                "the duration refusal is logged");
        }

        [TestMethod]
        public void RandomStart_EveryGoalTooShortForTheMinimum_IsInvalidDurations_NothingCurrent()
        {
            WorldEventManager.InitializeWith(() => (DurationStore(withLong: false), Catalog()));

            Assert.IsFalse(WorldEventManager.TryStart(LongMinimum(), out var evt, out var error, out var refusal));

            Assert.AreEqual(WorldEventStartRefusal.InvalidDurations, refusal, error);
            StringAssert.Contains(error, "700");
            Assert.IsNull(evt);
            Assert.IsNull(WorldEventManager.Current);
        }

        [TestMethod]
        public void PinnedStart_GoalTooShortForTheMinimum_IsOneAttempt_InvalidDurations()
        {
            WorldEventManager.InitializeWith(() => (DurationStore(withLong: true), Catalog()));

            var rule = new CountingRule(refuseFirst: 0);
            WorldEventObjectiveFactory.StartSpotRuleOverride = _ => rule;

            WorldEvent evt;
            WorldEventStartRefusal refusal;
            List<string> lines;

            using (var capture = new MarketLogCapture())
            {
                Assert.IsFalse(WorldEventManager.TryStart(LongMinimum(random: false), out evt, out _, out refusal));
                lines = capture.Events.Select(e => e.RenderedMessage).ToList();
            }

            Assert.AreEqual(WorldEventStartRefusal.InvalidDurations, refusal);
            Assert.AreEqual(0, rule.Asked.Count, "the duration check stops a pinned start before the start-spot rule");
            Assert.IsFalse(lines.Any(l => l.StartsWith("[WORLDEVENT] random candidate ")), "a pinned start never walks candidates");
            Assert.IsNull(evt);
            Assert.IsNull(WorldEventManager.Current);
        }

        [TestMethod]
        public void TryComposeForStart_Random_SkipsTheGoalThatCannotHoldTheMinimum_LikeTheLiveStart()
        {
            WorldEventManager.InitializeWith(() => (DurationStore(withLong: true), Catalog()));

            var seed = SeedWithShortFirst(DurationStore(withLong: true));
            WorldEventManager.RandomStartRngOverride = () => new Random(seed);

            Assert.IsTrue(WorldEventManager.TryComposeForStart(LongMinimum(), out var composition, out var error), error);
            Assert.AreEqual("long", composition.Goal.Id);

            // Only the short goal exists: the helper still returns a composition (the last one), and its duration check is
            // what refuses, exactly as the dry branch then reports it.
            WorldEventManager.InitializeWith(() => (DurationStore(withLong: false), Catalog()));

            Assert.IsTrue(WorldEventManager.TryComposeForStart(LongMinimum(), out composition, out error), error);
            Assert.AreEqual("short", composition.Goal.Id);
            Assert.IsFalse(WorldEvent.ValidateDurations(700, WorldEvent.ResolveMaxDuration(null, composition.Goal.MaxDurationSeconds), out _));

            Assert.IsNull(WorldEventManager.Current, "the helper starts nothing");
        }
    }
}
