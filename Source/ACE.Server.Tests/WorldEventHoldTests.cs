using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WeenieObject = ACE.Server.Physics.Common.WeenieObject;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the Hold goal ("protect the targets"): HoldObjective's success/fail/abort rules, the
    /// pure seeker and placement rules (WorldEventHoldRules), the goals.json protect block validation, the
    /// run lifecycle with a Hold objective (FailedObjectiveLost, success ahead of the timeout), the outcome
    /// and milestone lines (ASCII only - DreamWeave rule), and the engine guard that limits protected
    /// targets to their own run's monsters. Same approach as WorldEventObjectiveTests and
    /// WorldEventStateMachineTests: no database, no landblock, no live Creature (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventHoldTests
    {
        private const double T0 = 1_700_000_000d;

        // ---- builders ----------------------------------------------------------------------------------

        private static ProtectDef BuildProtect(int minSurvivors = 1, double seekShare = 0.3, int countBase = 1,
            double countPerParticipant = 0, int countCap = 3)
        {
            return new ProtectDef
            {
                Wcid = 2000001,
                Count = new ScaledCount { Base = countBase, PerParticipant = countPerParticipant, Cap = countCap },
                Health = new ScaledCount { Base = 20000, PerParticipant = 4000, Cap = 200000 },
                Radius = 6.0,
                MinSurvivors = minSurvivors,
                SeekShare = seekShare,
                Noun = "ward",
                NounPlural = "wards"
            };
        }

        private static GoalDef BuildHoldGoal(int holdSeconds = 420, ProtectDef protect = null, string progressTemplate = null,
            MvpRule rule = MvpRule.MostKills)
        {
            return new GoalDef
            {
                Id = "hold_wards",
                DisplayName = "Defend the Wards",
                Type = "Hold",
                TypeKind = GoalType.Hold,
                HoldSeconds = holdSeconds,
                MvpRule = rule.ToString(),
                RuleKind = rule,
                ProgressTemplate = progressTemplate,
                Protect = protect ?? BuildProtect()
            };
        }

        /// <summary>A HoldObjective over plain lists the test mutates, with a settable Active time.</summary>
        private sealed class HoldHarness
        {
            public readonly List<uint> Placed = new List<uint>();
            public readonly List<uint> Live = new List<uint>();
            public double ActiveAt = T0;
            public readonly HoldObjective Objective;

            /// <param name="activeAt">Where the Active time comes from; null reads <see cref="ActiveAt"/>.</param>
            public HoldHarness(GoalDef goal, Func<double> activeAt = null)
            {
                Objective = new HoldObjective(goal, () => Placed, () => Live, activeAt ?? (() => ActiveAt), null);
            }

            public void Place(params uint[] guids)
            {
                Placed.AddRange(guids);
                Live.AddRange(guids);
            }

            public void Kill(uint guid) => Live.Remove(guid);
        }

        // ---- HoldObjective: success, fail, abort ---------------------------------------------------------

        [TestMethod]
        public void Hold_SucceedsAtHoldSeconds_WithSurvivors()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 420));
            h.Place(1, 2, 3);

            h.Objective.Tick(T0 + 419);
            Assert.IsFalse(h.Objective.IsComplete, "one second short of the hold");
            Assert.IsNull(h.Objective.FailureOutcome);

            h.Objective.Tick(T0 + 420);
            Assert.IsTrue(h.Objective.IsComplete, "the boundary itself completes the hold");
            Assert.IsNull(h.Objective.FailureOutcome);
        }

        [TestMethod]
        public void Hold_DefaultMinSurvivors_FailsOnlyWhenAllAreDead()
        {
            var h = new HoldHarness(BuildHoldGoal());
            h.Place(1, 2, 3);
            h.Objective.Tick(T0 + 10);

            h.Kill(1);
            h.Kill(2);
            Assert.IsNull(h.Objective.FailureOutcome, "one ward still stands, and minSurvivors defaults to 1");

            h.Kill(3);
            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, h.Objective.FailureOutcome);
            Assert.IsFalse(h.Objective.IsComplete);
        }

        [TestMethod]
        public void Hold_MinSurvivorsTwo_FailsAsSoonAsFewerThanTwoStand()
        {
            var h = new HoldHarness(BuildHoldGoal(protect: BuildProtect(minSurvivors: 2)));
            h.Place(1, 2, 3);
            h.Objective.Tick(T0 + 10);

            h.Kill(1);
            Assert.IsNull(h.Objective.FailureOutcome, "two of three still stand");

            h.Kill(2);
            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, h.Objective.FailureOutcome);
        }

        [TestMethod]
        public void Hold_MinSurvivorsIsClampedToTheNumberPlaced()
        {
            // minSurvivors 3, but only one ward made it into the world: the run is judged on that one.
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 60, protect: BuildProtect(minSurvivors: 3)));
            h.Place(7);

            h.Objective.Tick(T0 + 60);

            Assert.AreEqual(1, h.Objective.RequiredSurvivors);
            Assert.IsNull(h.Objective.FailureOutcome);
            Assert.IsTrue(h.Objective.IsComplete);
        }

        [TestMethod]
        public void RequiredFor_ClampsBetweenOneAndPlaced()
        {
            Assert.AreEqual(0, HoldObjective.RequiredFor(1, 0), "nothing placed, nothing required");
            Assert.AreEqual(1, HoldObjective.RequiredFor(1, 3));
            Assert.AreEqual(2, HoldObjective.RequiredFor(2, 3));
            Assert.AreEqual(3, HoldObjective.RequiredFor(5, 3));
            Assert.AreEqual(1, HoldObjective.RequiredFor(0, 3), "a minSurvivors below 1 still requires one");
        }

        [TestMethod]
        public void Hold_NothingPlaced_AbortsAfterTheGrace_AndNeverCompletes()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 60));

            h.Objective.Tick(T0 + 1);
            Assert.IsNull(h.Objective.FailureOutcome, "placement is queued on the landblock; give it a moment");

            h.Objective.Tick(T0 + HoldObjective.PlacementGraceSeconds);
            Assert.AreEqual(WorldEventOutcome.AbortedError, h.Objective.FailureOutcome);

            h.Objective.Tick(T0 + 600);
            Assert.IsFalse(h.Objective.IsComplete, "a run with nothing to protect is never a win");
        }

        [TestMethod]
        public void Hold_BeforeActive_IsNeitherCompleteNorFailed()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 1)) { ActiveAt = 0 };

            h.Objective.Tick(T0 + 1000);

            Assert.IsFalse(h.Objective.IsComplete);
            Assert.IsNull(h.Objective.FailureOutcome);
        }

        [TestMethod]
        public void Hold_LastWardDyingOnTheBoundary_IsAFailNotASuccess()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 60));
            h.Place(1);

            h.Kill(1);
            h.Objective.Tick(T0 + 60);

            Assert.IsFalse(h.Objective.IsComplete);
            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, h.Objective.FailureOutcome);
        }

        [TestMethod]
        public void Hold_NeverStopsTheWaves()
        {
            var h = new HoldHarness(BuildHoldGoal());
            h.Place(1);

            IWorldEventObjective objective = h.Objective;

            Assert.IsNull(objective.RemainingKills, "null is 'no opinion' - 0 would suppress every wave");
            Assert.IsNull(objective.TargetKills);
        }

        [TestMethod]
        public void Hold_WardDeath_IsNotRecordedAsTheKillingBlow()
        {
            var h = new HoldHarness(BuildHoldGoal(rule: MvpRule.KillingBlow));
            h.Place(1);

            h.Objective.OnDeathCore(100, wasEventCreature: true, "Alice", "Alice");
            h.Objective.OnDeathCore(1, wasEventCreature: true, "Drudge Ravener", "Drudge Ravener");

            Assert.AreEqual("Alice", h.Objective.Mvp().Name, "the monster that broke a ward is never the MVP");
        }

        // ---- progress text --------------------------------------------------------------------------------

        [TestMethod]
        public void ProgressText_DefaultTemplate_FillsEveryToken()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 420));
            h.Place(1, 2, 3);
            h.Kill(2);

            h.Objective.Tick(T0 + 55);

            Assert.AreEqual("2 of 3 wards stand. Hold for 6:05.", h.Objective.ProgressText);
        }

        [TestMethod]
        public void ProgressText_CustomTemplate_SupportsLostAndNoun()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 120,
                protect: BuildProtect(), progressTemplate: "{lost} {noun} down, {alive}/{total} left, {remaining} to go."));
            h.Place(1, 2);
            h.Kill(1);

            h.Objective.Tick(T0 + 120);

            Assert.AreEqual("1 ward down, 1/2 left, 0:00 to go.", h.Objective.ProgressText);
        }

        [TestMethod]
        public void ProgressText_NothingPlaced_SaysSo()
        {
            var h = new HoldHarness(BuildHoldGoal());

            Assert.AreEqual("no wards have been placed yet.", h.Objective.ProgressText);
        }

        [TestMethod]
        public void FormatRemaining_IsMinutesAndPaddedSeconds()
        {
            Assert.AreEqual("0:00", HoldObjective.FormatRemaining(0));
            Assert.AreEqual("0:00", HoldObjective.FormatRemaining(-5));
            Assert.AreEqual("1:05", HoldObjective.FormatRemaining(65));
            Assert.AreEqual("7:00", HoldObjective.FormatRemaining(420));
        }

        // ---- seekers and placement (WorldEventHoldRules) --------------------------------------------------

        [TestMethod]
        public void SeekerCount_AveragesToTheShareAcrossManySmallWaves()
        {
            var carry = 0.0;
            var seekers = 0;

            for (var wave = 0; wave < 100; wave++)
                seekers += WorldEventHoldRules.SeekerCount(3, 0.3, ref carry);

            // 300 monsters at 30% is 90; the carry may hold back at most one more.
            Assert.IsTrue(seekers >= 89 && seekers <= 90, $"expected about 90 seekers, got {seekers}");
        }

        [TestMethod]
        public void SeekerCount_ShareZeroIsNone_ShareOneIsAll()
        {
            var carry = 0.5;

            Assert.AreEqual(0, WorldEventHoldRules.SeekerCount(10, 0, ref carry));
            Assert.AreEqual(0.0, carry, "a zero share resets the carry");

            Assert.AreEqual(10, WorldEventHoldRules.SeekerCount(10, 1.0, ref carry));
            Assert.AreEqual(10, WorldEventHoldRules.SeekerCount(10, 2.0, ref carry), "above 1 reads as the whole wave");
            Assert.AreEqual(0, WorldEventHoldRules.SeekerCount(10, double.NaN, ref carry));
            Assert.AreEqual(0, WorldEventHoldRules.SeekerCount(0, 0.5, ref carry), "an empty wave has no seekers");
        }

        [TestMethod]
        public void SeekerCount_NeverExceedsTheWave()
        {
            var carry = 0.99;

            Assert.IsTrue(WorldEventHoldRules.SeekerCount(1, 0.99, ref carry) <= 1);
        }

        [TestMethod]
        public void PickIndices_AreDistinctAndInRange()
        {
            var rng = new Random(1234);

            for (var trial = 0; trial < 50; trial++)
            {
                var picked = WorldEventHoldRules.PickIndices(10, 4, rng);

                Assert.AreEqual(4, picked.Count);
                Assert.IsTrue(picked.All(i => i >= 0 && i < 10));
            }

            Assert.AreEqual(3, WorldEventHoldRules.PickIndices(3, 10, rng).Count, "clamped to the size");
            Assert.AreEqual(0, WorldEventHoldRules.PickIndices(5, 0, rng).Count);
            Assert.AreEqual(0, WorldEventHoldRules.PickIndices(0, 2, rng).Count);
        }

        [TestMethod]
        public void PickIndices_IsNotAlwaysTheFirstEntries()
        {
            var rng = new Random(42);
            var seenAboveTwo = false;

            for (var trial = 0; trial < 50 && !seenAboveTwo; trial++)
                seenAboveTwo = WorldEventHoldRules.PickIndices(10, 3, rng).Any(i => i > 2);

            Assert.IsTrue(seenAboveTwo, "the seekers must be a random draw, not the first trash picked");
        }

        [TestMethod]
        public void RingOffsets_OneTargetSitsOnTheAnchor()
        {
            var offsets = WorldEventHoldRules.RingOffsets(1, 6.0);

            Assert.AreEqual(1, offsets.Count);
            Assert.AreEqual(0f, offsets[0].Dx);
            Assert.AreEqual(0f, offsets[0].Dy);
        }

        [TestMethod]
        public void RingOffsets_SeveralTargetsAreEvenlySpacedAtTheRadius()
        {
            var offsets = WorldEventHoldRules.RingOffsets(3, 6.0);

            Assert.AreEqual(3, offsets.Count);

            foreach (var o in offsets)
                Assert.AreEqual(6.0, Math.Sqrt(o.Dx * o.Dx + o.Dy * o.Dy), 1e-4, "every target sits on the ring");

            Assert.AreEqual(0f, offsets[0].Dx, 1e-4f, "the first is due north");
            Assert.AreEqual(6f, offsets[0].Dy, 1e-4f);
            CollectionAssert.AreEqual(new[] { 0f, 120f, 240f }, offsets.Select(o => o.YawDegrees).ToArray());
        }

        [TestMethod]
        public void RingOffsets_RadiusZeroPutsEveryTargetOnTheAnchor_CountZeroIsEmpty()
        {
            Assert.IsTrue(WorldEventHoldRules.RingOffsets(3, 0).All(o => o.Dx == 0f && o.Dy == 0f));
            Assert.AreEqual(0, WorldEventHoldRules.RingOffsets(0, 6).Count);
        }

        // ---- ProtectDef ----------------------------------------------------------------------------------

        [TestMethod]
        public void ProtectDef_CountScalesWithPlayers_WithinOneToThree()
        {
            var protect = BuildProtect(countBase: 1, countPerParticipant: 0.2, countCap: 3);

            Assert.AreEqual(1, protect.ResolveCount(0));
            Assert.AreEqual(2, protect.ResolveCount(5));
            Assert.AreEqual(3, protect.ResolveCount(10));
            Assert.AreEqual(3, protect.ResolveCount(100), "capped");
            Assert.IsTrue(protect.ResolveCount(-4) >= 1);
        }

        [TestMethod]
        public void ProtectDef_HealthScalesWithPlayers()
        {
            var protect = BuildProtect();

            Assert.AreEqual(20000, protect.ResolveHealth(0));
            Assert.AreEqual(60000, protect.ResolveHealth(10));
            Assert.AreEqual(200000, protect.ResolveHealth(1000), "capped");
        }

        [TestMethod]
        public void ProtectDef_EffectiveSeekShare_HonoursTheLiveOverride()
        {
            var saved = WorldEventOverrides.DoubleSource;

            try
            {
                var protect = BuildProtect(seekShare: 0.3);

                WorldEventOverrides.DoubleSource = key => 0;
                Assert.AreEqual(0.3, protect.EffectiveSeekShare, 1e-9, "0 means use the JSON value");

                WorldEventOverrides.DoubleSource = key => key == "world_events_hold_seek_share" ? 0.75 : 0;
                Assert.AreEqual(0.75, protect.EffectiveSeekShare, 1e-9);

                WorldEventOverrides.DoubleSource = key => key == "world_events_hold_seek_share" ? 5 : 0;
                Assert.AreEqual(1.0, protect.EffectiveSeekShare, 1e-9, "values above 1 read as 1");
            }
            finally
            {
                WorldEventOverrides.DoubleSource = saved;
            }
        }

        [TestMethod]
        public void ProtectDef_NounDefaults()
        {
            var protect = new ProtectDef();

            Assert.AreEqual("ward", protect.EffectiveNoun);
            Assert.AreEqual("wards", protect.EffectiveNounPlural);
            Assert.AreEqual(1, protect.MinSurvivors);
            Assert.AreEqual(0.3, protect.SeekShare, 1e-9);
            Assert.IsTrue(protect.HealthMilestones);
        }

        // ---- goals.json protect block validation -----------------------------------------------------------

        private const string ValidProtectJson =
            @"""protect"": { ""wcid"": 2000001, ""count"": { ""base"": 1, ""perParticipant"": 0.2, ""cap"": 3 },
                ""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0,
                ""minSurvivors"": 1, ""seekShare"": 0.3, ""healthMilestones"": true, ""noun"": ""ward"", ""nounPlural"": ""wards"" }";

        private static string HoldGoalsJson(string protectJson, int holdSeconds = 420)
        {
            var protectPart = protectJson == null ? "" : ", " + protectJson;

            return @"{ ""goals"": [ { ""id"": ""hold_wards"", ""displayName"": ""Defend the Wards"", ""type"": ""Hold"",
                ""holdSeconds"": " + holdSeconds + @", ""mvpRule"": ""mostKills"",
                ""progressTemplate"": ""{alive} of {total} {nounPlural} stand. Hold for {remaining}.""" + protectPart + " } ] }";
        }

        [TestMethod]
        public void GoalsJson_ValidHoldGoal_Parses()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ValidProtectJson), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));

            var goal = store.Goals["hold_wards"];

            Assert.AreEqual(GoalType.Hold, goal.TypeKind);
            Assert.AreEqual(420, goal.HoldSeconds);
            Assert.AreEqual(2000001u, goal.Protect.Wcid);
            Assert.AreEqual(3, goal.Protect.Count.Cap);
            Assert.AreEqual(6.0, goal.Protect.Radius, 1e-9);
            Assert.AreEqual(0.3, goal.Protect.SeekShare, 1e-9);
            Assert.AreEqual("wards", goal.Protect.NounPlural);
            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("hold_wards")));
        }

        [TestMethod]
        public void GoalsJson_HoldWithoutProtect_IsDropped()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(null), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("hold_wards") && d.Contains("no protect block")));
        }

        [TestMethod]
        [DataRow(@"""wcid"": 0", "no wcid")]
        [DataRow(@"""count"": { ""base"": 0, ""perParticipant"": 0, ""cap"": 3 }", "protect.count")]
        [DataRow(@"""count"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 4 }", "protect.count")]
        [DataRow(@"""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 2 }", "protect.count")]
        [DataRow(@"""health"": { ""base"": 0, ""perParticipant"": 0, ""cap"": 10 }", "protect.health")]
        [DataRow(@"""radius"": -1", "protect.radius")]
        [DataRow(@"""seekShare"": 1.5", "protect.seekShare")]
        [DataRow(@"""seekShare"": -0.1", "protect.seekShare")]
        [DataRow(@"""minSurvivors"": 0", "protect.minSurvivors")]
        public void GoalsJson_InvalidProtectField_DropsTheGoal(string fieldJson, string expectedDiagnostic)
        {
            var key = fieldJson.Substring(0, fieldJson.IndexOf(':'));
            var fields = new Dictionary<string, string>
            {
                [@"""wcid"""] = @"""wcid"": 2000001",
                [@"""count"""] = @"""count"": { ""base"": 1, ""perParticipant"": 0.2, ""cap"": 3 }",
                [@"""health"""] = @"""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }",
                [@"""radius"""] = @"""radius"": 6.0",
                [@"""seekShare"""] = @"""seekShare"": 0.3",
                [@"""minSurvivors"""] = @"""minSurvivors"": 1"
            };

            fields[key] = fieldJson;

            var protectJson = @"""protect"": { " + string.Join(", ", fields.Values) + " }";

            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(protectJson), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), $"{fieldJson} must drop the goal");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("hold_wards") && d.Contains(expectedDiagnostic)),
                "diagnostics: " + string.Join(" | ", store.Diagnostics));
        }

        [TestMethod]
        public void GoalsJson_HoldSecondsZero_IsStillDropped()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ValidProtectJson, holdSeconds: 0), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("hold_wards") && d.Contains("holdSeconds")));
        }

        [TestMethod]
        public void GoalsJson_NonHoldGoalIgnoresAProtectBlock()
        {
            var json = @"{ ""goals"": [ { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
                ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                ""mvpRule"": ""mostKills"", ""progressTemplate"": ""t"", ""protect"": { ""wcid"": 0 } } ] }";

            var store = WorldEventAxisStore.Parse(null, null, json, null, null);

            Assert.IsTrue(store.Goals.ContainsKey("kill_count"), "only a Hold goal validates its protect block");
        }

        // ---- start-time duration check ----------------------------------------------------------------------

        [TestMethod]
        public void ValidateHoldDuration_RefusesAHoldLongerThanTheRun()
        {
            Assert.IsTrue(WorldEvent.ValidateHoldDuration(BuildHoldGoal(holdSeconds: 600), 600, out var ok));
            Assert.IsNull(ok);

            Assert.IsFalse(WorldEvent.ValidateHoldDuration(BuildHoldGoal(holdSeconds: 601), 600, out var error));
            StringAssert.Contains(error, "hold_wards");
            AssertAscii(error);

            Assert.IsTrue(WorldEvent.ValidateHoldDuration(null, 1, out _), "no goal, nothing to check");
            Assert.IsTrue(WorldEvent.ValidateHoldDuration(new GoalDef { TypeKind = GoalType.KillCount, HoldSeconds = 9999 }, 1, out _),
                "only Hold goals are checked");
        }

        // ---- the run lifecycle with a Hold objective ---------------------------------------------------------

        private static WorldEvent BuildHoldEvent(GoalDef goal, IWorldEventObjective objective, int maxDurationSeconds = 600)
        {
            var source = new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "edges",
                GeometryKind = SourceGeometry.Edges,
                GeometryRadius = 45f,
                GeometryPoints = 3,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                RewardRadius = 60f,
                CompatibleGoals = new List<string> { goal.Id },
                StartFlavour = "Something is coming near {anchor}.",
                WaveFlavour = "Another rank arrives!"
            };

            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string> { "volcanic" },
                Members = new List<FamilyMember> { new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 } }
            };

            var reward = new RewardDef
            {
                Id = "standard",
                DisplayName = "Hammer Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = 120,
                GateByCharacter = true,
                GateByAccount = true,
                GateByIp = true
            };

            var anchor = new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() };

            var composition = new WorldEventComposition(source, new[] { family }, BossDef.None, goal, reward, anchor,
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = goal.Id,
                AnnounceLeadSeconds = 0,
                MaxDurationSeconds = maxDurationSeconds,
                MinDurationSeconds = 300,
                Invoker = "test"
            };

            // presenceCounter 1: a player is standing at the anchor, so the abandon rule never fires and the
            // Hold rules alone decide each run below.
            return new WorldEvent(1, composition, request, objective,
                () => new AudienceEstimate(3, 100, 150), () => T0, presenceCounter: () => 1);
        }

        /// <summary>A HoldObjective whose start time follows the event it is attached to.</summary>
        private static (WorldEvent Evt, HoldHarness Harness) BuildLiveHoldRun(int holdSeconds, int maxDurationSeconds)
        {
            var goal = BuildHoldGoal(holdSeconds: holdSeconds);

            WorldEvent evt = null;

            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);

            evt = BuildHoldEvent(goal, harness.Objective, maxDurationSeconds);

            return (evt, harness);
        }

        [TestMethod]
        public void Run_AllWardsLost_FinishesWithFailedObjectiveLost()
        {
            var (evt, harness) = BuildLiveHoldRun(holdSeconds: 420, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            harness.Place(1, 2);
            evt.Tick(T0 + 10);
            Assert.AreEqual(WorldEventState.Active, evt.State, "both wards stand");

            harness.Kill(1);
            evt.Tick(T0 + 20);
            Assert.AreEqual(WorldEventState.Active, evt.State, "one ward still stands");

            harness.Kill(2);
            evt.Tick(T0 + 30);

            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, evt.Outcome);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "a Failed* outcome still opens the consolation claim window");
        }

        [TestMethod]
        public void Run_HoldReachesItsTime_Succeeds_EvenBeforeTheMinimumDuration()
        {
            // holdSeconds 120 sits below the 300 s minimum duration: Hold is not held open by the minimum
            // (CompletionAllowed), so the run finishes as soon as the hold is served.
            var (evt, harness) = BuildLiveHoldRun(holdSeconds: 120, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1);

            evt.Tick(T0 + 119);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            evt.Tick(T0 + 120);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        [TestMethod]
        public void Run_HoldEndingOnTheTimeoutTick_IsASuccessNotATimeout()
        {
            var (evt, harness) = BuildLiveHoldRun(holdSeconds: 600, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            evt.Tick(T0 + 600);

            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome,
                "the objective is judged before the timers on the same tick");
        }

        [TestMethod]
        public void Run_NothingPlaced_IsAbortedNotFailed()
        {
            var (evt, _) = BuildLiveHoldRun(holdSeconds: 420, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);

            evt.Tick(T0 + HoldObjective.PlacementGraceSeconds);

            Assert.AreEqual(WorldEventOutcome.AbortedError, evt.Outcome);
        }

        [TestMethod]
        public void BossWins_IncludesFailedObjectiveLost()
        {
            Assert.IsTrue(WorldEvent.BossWins(WorldEventOutcome.FailedObjectiveLost));
            Assert.IsTrue(WorldEvent.BossWins(WorldEventOutcome.FailedTimeout), "existing outcomes unchanged");
            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.Success));
            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.AbortedError));
        }

        [TestMethod]
        public void FailedObjectiveLost_IsAppendedSoNoExistingOutcomeChangesItsNumber()
        {
            Assert.AreEqual(8, (int)WorldEventOutcome.AbortedError);
            Assert.AreEqual(9, (int)WorldEventOutcome.FailedObjectiveLost);
        }

        // ---- announcements (ASCII only) -----------------------------------------------------------------

        private static void AssertAscii(string text)
        {
            Assert.IsNotNull(text);
            Assert.IsFalse(text.Contains((char)0x2013), $"en dash in player-facing text: {text}");
            Assert.IsFalse(text.Contains((char)0x2014), $"em dash in player-facing text: {text}");
            Assert.IsTrue(text.All(c => c < 128), $"non-ASCII character in player-facing text: {text}");
        }

        [TestMethod]
        public void OutcomeLine_FailedObjectiveLost_NamesTheWardsAndTheConsolationCache()
        {
            var goal = BuildHoldGoal();

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedObjectiveLost, goal, "Holtburg",
                WorldEventMvp.None, null, 4, noun: "ward", nounPlural: "wards");

            StringAssert.Contains(line, "Defend the Wards at Holtburg failed - the wards were destroyed.");
            StringAssert.Contains(line, "consolation cache");
            AssertAscii(line);
        }

        [TestMethod]
        public void OutcomeLine_FailedObjectiveLost_UsesTheGoalFailTemplate()
        {
            var goal = BuildHoldGoal();

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedObjectiveLost, goal, "Holtburg",
                WorldEventMvp.None, null, 4, failTemplate: "The {nounPlural} at {anchor} fell - {reason}.",
                noun: "ward", nounPlural: "wards");

            StringAssert.Contains(line, "The wards at Holtburg fell - the wards were destroyed.");
            AssertAscii(line);
        }

        [TestMethod]
        public void HoldText_IsAllAscii()
        {
            AssertAscii(WorldEventAnnouncer.ObjectiveLostReason("wards"));
            AssertAscii(WorldEventAnnouncer.ObjectiveLostReason(null));
            AssertAscii(WorldEventAnnouncer.ProtectedHealthMilestoneLine("ward", "Holtburg", 50));
            AssertAscii(WorldEventAnnouncer.ProtectedHealthMilestoneLine(null, "Holtburg", 25));
            AssertAscii(HoldObjective.DefaultProgressTemplate);

            var h = new HoldHarness(BuildHoldGoal());
            AssertAscii(h.Objective.ProgressText);

            h.Place(1, 2);
            h.Objective.Tick(T0 + 30);
            AssertAscii(h.Objective.ProgressText);

            AssertAscii(WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, BuildHoldGoal(), "Holtburg",
                WorldEventMvp.None, null, 3, successTemplate: "The {nounPlural} at {anchor} held!", noun: "ward", nounPlural: "wards"));
        }

        [TestMethod]
        public void HoldErrorText_IsAllAscii()
        {
            // The refusals an admin sees in chat or the load log: the start-time duration check, the
            // factory's missing-protect refusal, and every protect-block diagnostic.
            WorldEvent.ValidateHoldDuration(BuildHoldGoal(holdSeconds: 9999), 600, out var durationError);
            AssertAscii(durationError);

            var noProtect = new GoalDef { Id = "hold", DisplayName = "Hold", Type = "Hold", TypeKind = GoalType.Hold, HoldSeconds = 60 };
            Assert.IsNull(WorldEventObjectiveFactory.Create(BuildHoldEvent(noProtect, null), out var factoryError));
            AssertAscii(factoryError);

            foreach (var reason in new[]
            {
                WorldEventAxisStore.ValidateProtect(null),
                WorldEventAxisStore.ValidateProtect(new ProtectDef()),
                WorldEventAxisStore.ValidateProtect(new ProtectDef { Wcid = 1 }),
                WorldEventAxisStore.ValidateProtect(new ProtectDef { Wcid = 1, Count = new ScaledCount { Base = 1, Cap = 3 } }),
                WorldEventAxisStore.ValidateProtect(new ProtectDef
                {
                    Wcid = 1, Count = new ScaledCount { Base = 1, Cap = 3 }, Health = new ScaledCount { Base = 1, Cap = 1 }, Radius = -1
                })
            })
            {
                Assert.IsNotNull(reason);
                AssertAscii(reason);
            }
        }

        [TestMethod]
        public void ProtectedHealthMilestoneLine_Wording()
        {
            var line = WorldEventAnnouncer.ProtectedHealthMilestoneLine("ward", "Holtburg", 50);

            StringAssert.EndsWith(line, "A ward at Holtburg is at 50% health!");
        }

        [TestMethod]
        public void Composition_HoldGoal_UsesTheProtectNouns()
        {
            var evt = BuildHoldEvent(BuildHoldGoal(protect: new ProtectDef
            {
                Wcid = 1, Count = new ScaledCount { Base = 1, Cap = 1 }, Health = new ScaledCount { Base = 1, Cap = 1 },
                Noun = "totem", NounPlural = "totems"
            }), null);

            Assert.AreEqual("totem", evt.Composition.ObjectiveNoun);
            Assert.AreEqual("totems", evt.Composition.ObjectiveNounPlural);
        }

        // ---- engine guard: only the run's own monsters ----------------------------------------------------

        [TestMethod]
        public void IsProtectedTargetFor_OnlyTheSameRunsMonsters()
        {
            Assert.IsTrue(WeenieObject.IsProtectedTargetFor(true, 7, attackerIsMonster: true, attackerRunId: 7));

            Assert.IsFalse(WeenieObject.IsProtectedTargetFor(true, 7, true, 8), "a monster from another run");
            Assert.IsFalse(WeenieObject.IsProtectedTargetFor(true, 7, true, 0), "an ordinary monster, no run stamp");
            Assert.IsFalse(WeenieObject.IsProtectedTargetFor(true, 7, false, 7), "a non-monster (player, pet, npc)");
            Assert.IsFalse(WeenieObject.IsProtectedTargetFor(true, 0, true, 0), "an unstamped target is never protected");
            Assert.IsFalse(WeenieObject.IsProtectedTargetFor(false, 7, true, 7), "a creature that is not protected");
        }

        [TestMethod]
        public void IsProtectedTargetFor_NullOrDefaultObjectsAreNeverProtected()
        {
            var plain = new WeenieObject();

            Assert.IsFalse(plain.IsProtectedTargetFor(new WeenieObject { IsMonster = true }));
            Assert.IsFalse(plain.IsProtectedTargetFor(null));

            var ward = new WeenieObject { IsWorldEventProtected = true, WorldEventRunId = 3 };

            Assert.IsTrue(ward.IsProtectedTargetFor(new WeenieObject { IsMonster = true, WorldEventRunId = 3 }));
            Assert.IsFalse(ward.IsProtectedTargetFor(new WeenieObject { IsMonster = true, WorldEventRunId = 4 }));
            Assert.IsFalse(ward.IsProtectedTargetFor(null));
        }

        [TestMethod]
        public void Throughput_ProtectedTargetsNeverCount()
        {
            Assert.IsFalse(WorldEventThroughput.Counts(10, 99, isDecor: false, isNpc: false, isProtected: true));
            Assert.IsTrue(WorldEventThroughput.Counts(10, 99, isDecor: false, isNpc: false, isProtected: false));
            Assert.IsTrue(WorldEventThroughput.Counts(10, 99, false, false), "the old overload is unchanged");
        }

        // ---- caravan lines: {family} and friends --------------------------------------------------------

        /// <summary>Family display names as WorldEventComposition.FamilyDisplayName composes them, one and two families.</summary>
        private static readonly string[] SampleFamilies =
        {
            "the Emberwrought", "the Drudges", "the Blackwing and the Drudges", "the Virindi", null
        };

        /// <summary>The caravan lines shipped in dreamweave-content/events/defend_caravan.goal.json.</summary>
        private static readonly string[] CaravanLines =
        {
            "Here come {family}! Keep them away from the wagons!",
            "We will never reach the market without your help. Hold them off!",
            "Stand with us, friends. Do not let {family} near these goods!",
            "We are down to {pct} percent! Hurry!",
            "Get {family} off this wagon!",
            "We cannot take much more of this!",
            "We are lost! Save the others!",
            "The wagon is gone... protect the rest!",
            "Curse {family}! Do not let them take the others!",
            "We made it! You have our thanks, friends.",
            "That is the last we will see of {family} today. Thank you!",
            "Driven off at last. Safe travels, and thank you.",
            "{alive} of {total} {nounPlural} still stand. Hold off {family} for {remaining}."
        };

        [TestMethod]
        public void FillLine_FamilyToken_UsesTheRunsFamily()
        {
            Assert.AreEqual("Here come the Emberwrought! Keep them away from the wagons!",
                WorldEventHoldRules.FillLine("Here come {family}! Keep them away from the wagons!", "the Emberwrought",
                    "caravan", "caravans", "Holtburg", 3, 3));

            Assert.AreEqual("Get the Blackwing and the Drudges off this wagon!",
                WorldEventHoldRules.FillLine("Get {family} off this wagon!", "the Blackwing and the Drudges",
                    "caravan", "caravans", "Holtburg", 2, 3), "a two-family run reads naturally too");
        }

        [TestMethod]
        public void FillLine_CapitalFamilyToken_StartsASentence()
        {
            Assert.AreEqual("The Emberwrought will not take this caravan!",
                WorldEventHoldRules.FillLine("{Family} will not take this caravan!", "the Emberwrought", "caravan", "caravans", null, 3, 3));
        }

        [TestMethod]
        public void FillLine_NoFamily_FallsBackToAGenericPhrase()
        {
            Assert.AreEqual("Here come the attackers!", WorldEventHoldRules.FillLine("Here come {family}!", null, "c", "cs", null, 0, 0));
            Assert.AreEqual("The attackers are here!", WorldEventHoldRules.FillLine("{Family} are here!", "  ", "c", "cs", null, 0, 0));
        }

        [TestMethod]
        public void FillLine_CountsNounsAnchorAndPct()
        {
            Assert.AreEqual("2 of 3 caravans at Holtburg, 50 percent.",
                WorldEventHoldRules.FillLine("{alive} of {total} {nounPlural} at {anchor}, {pct} percent.", "the Drudges",
                    "caravan", "caravans", "Holtburg", 2, 3, pct: 50));

            Assert.AreEqual("{pct} and {anchor} stay put",
                WorldEventHoldRules.FillLine("{pct} and {anchor} stay put", "the Drudges", "c", "cs", null, 0, 0),
                "{pct} only fills on health lines and {anchor} only when there is one");

            Assert.AreEqual("", WorldEventHoldRules.FillLine(null, "the Drudges", "c", "cs", null, 0, 0));
        }

        [TestMethod]
        public void CaravanLines_ReadForEveryFamily_AsciiAndNoLeftoverTokens()
        {
            foreach (var family in SampleFamilies)
            {
                foreach (var template in CaravanLines)
                {
                    var line = WorldEventHoldRules.FillLine(template.Replace("{remaining}", "6:05"), family,
                        "caravan", "caravans", "Holtburg", 2, 3, pct: 50);

                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), $"unfilled token in \"{line}\"");
                    Assert.IsFalse(line.IndexOf("bandit", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"a caravan line must never hardcode a family: \"{line}\"");
                    AssertAscii(line);
                }
            }
        }

        [TestMethod]
        public void CapitalizeFirst_OnlyTouchesTheFirstCharacter()
        {
            Assert.AreEqual("The Blackwing and the Drudges", WorldEventHoldRules.CapitalizeFirst("the Blackwing and the Drudges"));
            Assert.AreEqual("", WorldEventHoldRules.CapitalizeFirst(""));
            Assert.AreEqual("", WorldEventHoldRules.CapitalizeFirst(null));
        }

        [TestMethod]
        public void PickLine_SkipsBlanks_NullWhenNothingToSay()
        {
            var rng = new Random(7);

            Assert.IsNull(WorldEventHoldRules.PickLine(null, rng));
            Assert.IsNull(WorldEventHoldRules.PickLine(new List<string>(), rng));
            Assert.IsNull(WorldEventHoldRules.PickLine(new List<string> { "", "  " }, rng));

            for (var i = 0; i < 20; i++)
                Assert.AreEqual("only", WorldEventHoldRules.PickLine(new List<string> { " ", "only", "" }, rng));

            var seen = new HashSet<string>();

            for (var i = 0; i < 100; i++)
                seen.Add(WorldEventHoldRules.PickLine(new List<string> { "a", "b", "c" }, rng));

            Assert.AreEqual(3, seen.Count, "every line in a pool gets picked");
        }

        [TestMethod]
        public void RepairDashes_ReplacesEmAndEnDashes()
        {
            Assert.IsTrue(WorldEventHoldRules.RepairDashes("Hold " + (char)0x2014 + " now " + (char)0x2013 + " go", out var fixedText));
            Assert.AreEqual("Hold - now - go", fixedText);

            Assert.IsFalse(WorldEventHoldRules.RepairDashes("Already - fine", out var same));
            Assert.AreEqual("Already - fine", same);
            Assert.IsFalse(WorldEventHoldRules.RepairDashes(null, out _));
        }

        // ---- the caravan goal: exactly three, lines from goals.json -------------------------------------

        private const string CaravanProtectJson =
            @"""protect"": { ""wcid"": 2000002, ""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 3 },
                ""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0,
                ""noun"": ""caravan"", ""nounPlural"": ""caravans"",
                ""lines"": { ""spawn"": [ ""Here come {family}!"", """" ],
                             ""health"": [ ""We are down to {pct} percent! DASH"" ],
                             ""destroyed"": [ ""We are lost!"" ],
                             ""success"": [ ""We made it!"" ] } }";

        [TestMethod]
        public void CaravanGoal_AlwaysPlacesExactlyThree()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(CaravanProtectJson), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));

            var protect = store.Goals["hold_wards"].Protect;

            foreach (var players in new[] { 0, 1, 5, 20, 200 })
                Assert.AreEqual(3, protect.ResolveCount(players), $"{players} players");

            Assert.IsTrue(protect.ResolveHealth(20) > protect.ResolveHealth(1), "health still scales with players");
        }

        [TestMethod]
        public void CaravanGoal_LinesParse_BlanksDropped_DashesRepaired()
        {
            var json = HoldGoalsJson(CaravanProtectJson.Replace("DASH", ((char)0x2014).ToString()));

            var store = WorldEventAxisStore.Parse(null, null, json, null, null);

            var lines = store.Goals["hold_wards"].Protect.Lines;

            CollectionAssert.AreEqual(new[] { "Here come {family}!" }, lines.Spawn, "the blank spawn line is removed");
            CollectionAssert.AreEqual(new[] { "We are down to {pct} percent! -" }, lines.Health, "the em dash became a hyphen");
            CollectionAssert.AreEqual(new[] { "We are lost!" }, lines.Destroyed);
            CollectionAssert.AreEqual(new[] { "We made it!" }, lines.Success);

            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.lines.spawn") && d.Contains("blank")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.lines.health") && d.Contains("hyphen")));
            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), "line repairs never drop the goal");
        }

        [TestMethod]
        public void CaravanGoal_MissingLinesOrNullPools_AreFine()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ValidProtectJson), null, null);
            Assert.IsNull(store.Goals["hold_wards"].Protect.Lines, "no lines block, nothing is said");

            var nullPools = ValidProtectJson.Replace(@"""wcid"": 2000001,", @"""wcid"": 2000001, ""lines"": { ""spawn"": null },");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(nullPools), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(0, store.Goals["hold_wards"].Protect.Lines.Spawn.Count, "a null pool reads as empty");
        }

        [TestMethod]
        public void ComposeProtectedLine_FillsTheRunsFamilyAndAnchor()
        {
            var goal = BuildHoldGoal(protect: new ProtectDef
            {
                Wcid = 2000002,
                Count = new ScaledCount { Base = 3, Cap = 3 },
                Health = new ScaledCount { Base = 1, Cap = 1 },
                Noun = "caravan",
                NounPlural = "caravans",
                Lines = new ProtectLinesDef { Spawn = new List<string> { "{Family} at {anchor}! Guard the {nounPlural}!" } }
            });

            var evt = BuildHoldEvent(goal, null);

            Assert.AreEqual("The Emberwrought at the test anchor! Guard the caravans!",
                evt.ComposeProtectedLine(goal.Protect.Lines.Spawn, null));

            Assert.IsNull(evt.ComposeProtectedLine(goal.Protect.Lines.Success, null), "an empty pool says nothing");
            Assert.IsNull(evt.ComposeProtectedLine(null, null));
        }

        [TestMethod]
        public void HoldProgress_SupportsTheFamilyToken()
        {
            var goal = BuildHoldGoal(holdSeconds: 420, progressTemplate: CaravanLines[CaravanLines.Length - 1],
                protect: new ProtectDef
                {
                    Wcid = 2000002, Count = new ScaledCount { Base = 3, Cap = 3 }, Health = new ScaledCount { Base = 1, Cap = 1 },
                    Noun = "caravan", NounPlural = "caravans"
                });

            var placed = new List<uint> { 1, 2, 3 };
            var live = new List<uint> { 1, 3 };

            var objective = new HoldObjective(goal, () => placed, () => live, () => T0, null, () => "the Drudges");
            objective.Tick(T0 + 55);

            Assert.AreEqual("2 of 3 caravans still stand. Hold off the Drudges for 6:05.", objective.ProgressText);
        }

        [TestMethod]
        public void OutcomeTemplates_SupportTheFamilyToken()
        {
            var goal = BuildHoldGoal();

            var success = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "Holtburg", WorldEventMvp.None,
                null, 3, successTemplate: "{Family} were driven from {anchor}!", noun: "caravan", nounPlural: "caravans",
                family: "the Emberwrought");

            StringAssert.Contains(success, "The Emberwrought were driven from Holtburg!");

            var fail = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedObjectiveLost, goal, "Holtburg", WorldEventMvp.None,
                null, 3, failTemplate: "{family} took the {nounPlural} - {reason}.", noun: "caravan", nounPlural: "caravans",
                family: "the Blackwing and the Drudges");

            StringAssert.Contains(fail, "the Blackwing and the Drudges took the caravans - the caravans were destroyed.");
            AssertAscii(success);
            AssertAscii(fail);
        }

        [TestMethod]
        public void ProtectedSpeechLine_GlobalCopyFormat()
        {
            var line = WorldEventAnnouncer.ProtectedSpeechLine("Caravan Merchant", "We made it!");

            StringAssert.EndsWith(line, "Caravan Merchant: \"We made it!\"");
            AssertAscii(line);

            StringAssert.EndsWith(WorldEventAnnouncer.ProtectedSpeechLine(null, "Help!"), "Ward: \"Help!\"");
        }
    }
}
