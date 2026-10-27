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
    public partial class WorldEventHoldTests
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

            /// <summary>Wave counts the objective reads (wave-based holds).</summary>
            public HoldWaveCounts Waves;

            /// <param name="activeAt">Where the Active time comes from; null reads <see cref="ActiveAt"/>.</param>
            public HoldHarness(GoalDef goal, Func<double> activeAt = null)
            {
                Objective = new HoldObjective(goal, () => Placed, () => Live, activeAt ?? (() => ActiveAt), null,
                    null, () => Waves);
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
            // Outward headings in NpcRotation's counter-clockwise sense: north 0, then the targets at compass
            // 120 (ESE) and 240 (WSW) face yaw 240 and 120.
            CollectionAssert.AreEqual(new[] { 0f, 240f, 120f }, offsets.Select(o => o.YawDegrees).ToArray());
        }

        [TestMethod]
        public void RingOffsets_EveryTargetFacesAwayFromTheCentre()
        {
            foreach (var count in new[] { 2, 3 })
            {
                foreach (var o in WorldEventHoldRules.RingOffsets(count, 6.0))
                {
                    // A Position's facing is Vector3.Transform(UnitY, Rotation) - the same rule the client uses.
                    var facing = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY,
                        ACE.Server.WorldEvents.WorldEventSpawner.NpcRotation(o.YawDegrees));

                    var outward = new System.Numerics.Vector2(o.Dx, o.Dy) / 6f;

                    Assert.AreEqual(outward.X, facing.X, 1e-3, $"target at ({o.Dx:F2}, {o.Dy:F2}) must face outward");
                    Assert.AreEqual(outward.Y, facing.Y, 1e-3, $"target at ({o.Dx:F2}, {o.Dy:F2}) must face outward");
                }
            }
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

        private static WorldEvent BuildHoldEvent(GoalDef goal, IWorldEventObjective objective, int maxDurationSeconds = 600,
            int abandonAfterSeconds = WorldEvent.DefaultAbandonAfterSeconds, Func<int> presenceCounter = null,
            int? testLevel = null, Func<AudienceEstimate> audience = null)
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
                AbandonAfterSeconds = abandonAfterSeconds,
                TestLevel = testLevel,
                Invoker = "test"
            };

            // presenceCounter defaults to 1: a player is standing at the anchor, so the abandon rule never
            // fires and the Hold rules alone decide each run below unless a test says otherwise.
            return new WorldEvent(1, composition, request, objective,
                audience ?? (() => new AudienceEstimate(3, 100, 150)), () => T0, presenceCounter: presenceCounter ?? (() => 1));
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

            // Seen live 2026-10-19 as "A envoy at Holtburg ...".
            StringAssert.EndsWith(WorldEventAnnouncer.ProtectedHealthMilestoneLine("envoy", "Holtburg (42.7N, 32.8E)", 75),
                "An envoy at Holtburg (42.7N, 32.8E) is at 75% health!");
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

        // ---- envoy lines: {family} and friends --------------------------------------------------------

        /// <summary>Family display names as WorldEventComposition.FamilyDisplayName composes them, one and two families.</summary>
        private static readonly string[] SampleFamilies =
        {
            "the Emberwrought", "the Drudges", "the Blackwing and the Drudges", "the Virindi", null
        };

        /// <summary>Sample target lines and progress templates, every token in use.</summary>
        private static readonly string[] EnvoyLines =
        {
            "Here come {family}! Keep them away from the posts!",
            "We will never reach the market without your help. Hold them off!",
            "Stand with us, friends. Do not let {family} near these goods!",
            "We are down to {pct} percent! Hurry!",
            "Get {family} off this post!",
            "We cannot take much more of this!",
            "We are lost! Save the others!",
            "The post is gone... protect the rest!",
            "Curse {family}! Do not let them take the others!",
            "We made it! You have our thanks, friends.",
            "That is the last we will see of {family} today. Thank you!",
            "Driven off at last. Safe travels, and thank you.",
            "A moment to breathe. Patch the posts, quickly!",
            "They fall back! Mend what you can before the next rush.",
            "Well fought. Bind the damage before {family} return.",
            "{waveProgress} - {alive} of {total} {nounPlural} still stand against {family}.",
            // The time-only progress line, kept last: HoldProgress_SupportsTheFamilyToken reads it.
            "{alive} of {total} {nounPlural} still stand. Hold off {family} for {remaining}."
        };

        [TestMethod]
        public void FillLine_FamilyToken_UsesTheRunsFamily()
        {
            Assert.AreEqual("Here come the Emberwrought! Keep them away from the posts!",
                WorldEventHoldRules.FillLine("Here come {family}! Keep them away from the posts!", "the Emberwrought",
                    "envoy", "envoys", "Holtburg", 3, 3));

            Assert.AreEqual("Get the Blackwing and the Drudges off this post!",
                WorldEventHoldRules.FillLine("Get {family} off this post!", "the Blackwing and the Drudges",
                    "envoy", "envoys", "Holtburg", 2, 3), "a two-family run reads naturally too");
        }

        [TestMethod]
        public void FillLine_CapitalFamilyToken_StartsASentence()
        {
            Assert.AreEqual("The Emberwrought will not take this envoy!",
                WorldEventHoldRules.FillLine("{Family} will not take this envoy!", "the Emberwrought", "envoy", "envoys", null, 3, 3));
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
            Assert.AreEqual("2 of 3 envoys at Holtburg, 50 percent.",
                WorldEventHoldRules.FillLine("{alive} of {total} {nounPlural} at {anchor}, {pct} percent.", "the Drudges",
                    "envoy", "envoys", "Holtburg", 2, 3, pct: 50));

            Assert.AreEqual("{pct} and {anchor} stay put",
                WorldEventHoldRules.FillLine("{pct} and {anchor} stay put", "the Drudges", "c", "cs", null, 0, 0),
                "{pct} only fills on health lines and {anchor} only when there is one");

            Assert.AreEqual("", WorldEventHoldRules.FillLine(null, "the Drudges", "c", "cs", null, 0, 0));
        }

        [TestMethod]
        public void EnvoyLines_ReadForEveryFamily_AsciiAndNoLeftoverTokens()
        {
            foreach (var family in SampleFamilies)
            {
                foreach (var template in EnvoyLines)
                {
                    var line = WorldEventHoldRules.FillLine(template.Replace("{remaining}", "6:05")
                            .Replace("{waveProgress}", WorldEventHoldRules.WaveProgress(3, 7)), family,
                        "envoy", "envoys", "Holtburg", 2, 3, pct: 50);

                    Assert.IsFalse(line.Contains('{') || line.Contains('}'), $"unfilled token in \"{line}\"");
                    Assert.IsFalse(line.IndexOf("bandit", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"a envoy line must never hardcode a family: \"{line}\"");
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

        // ---- the envoy goal: exactly three, lines from goals.json -------------------------------------

        private const string EnvoyProtectJson =
            @"""protect"": { ""wcid"": 2000002, ""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 3 },
                ""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0,
                ""noun"": ""envoy"", ""nounPlural"": ""envoys"",
                ""lines"": { ""spawn"": [ ""Here come {family}!"", """" ],
                             ""health"": [ ""We are down to {pct} percent! DASH"" ],
                             ""destroyed"": [ ""We are lost!"" ],
                             ""success"": [ ""We made it!"" ] } }";

        [TestMethod]
        public void EnvoyGoal_AlwaysPlacesExactlyThree()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(EnvoyProtectJson), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));

            var protect = store.Goals["hold_wards"].Protect;

            foreach (var players in new[] { 0, 1, 5, 20, 200 })
                Assert.AreEqual(3, protect.ResolveCount(players), $"{players} players");

            Assert.IsTrue(protect.ResolveHealth(20) > protect.ResolveHealth(1), "health still scales with players");
        }

        [TestMethod]
        public void EnvoyGoal_LinesParse_BlanksDropped_DashesRepaired()
        {
            var json = HoldGoalsJson(EnvoyProtectJson.Replace("DASH", ((char)0x2014).ToString()));

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
        public void EnvoyGoal_MissingLinesOrNullPools_AreFine()
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
                Noun = "envoy",
                NounPlural = "envoys",
                Lines = new ProtectLinesDef { Spawn = new List<string> { "{Family} at {anchor}! Guard the {nounPlural}!" } }
            });

            var evt = BuildHoldEvent(goal, null);

            Assert.AreEqual("The Emberwrought at the test anchor! Guard the envoys!",
                evt.ComposeProtectedLine(goal.Protect.Lines.Spawn, null));

            Assert.IsNull(evt.ComposeProtectedLine(goal.Protect.Lines.Success, null), "an empty pool says nothing");
            Assert.IsNull(evt.ComposeProtectedLine(null, null));
        }

        [TestMethod]
        public void HoldProgress_SupportsTheFamilyToken()
        {
            var goal = BuildHoldGoal(holdSeconds: 420, progressTemplate: EnvoyLines[EnvoyLines.Length - 1],
                protect: new ProtectDef
                {
                    Wcid = 2000002, Count = new ScaledCount { Base = 3, Cap = 3 }, Health = new ScaledCount { Base = 1, Cap = 1 },
                    Noun = "envoy", NounPlural = "envoys"
                });

            var placed = new List<uint> { 1, 2, 3 };
            var live = new List<uint> { 1, 3 };

            var objective = new HoldObjective(goal, () => placed, () => live, () => T0, null, () => "the Drudges");
            objective.Tick(T0 + 55);

            Assert.AreEqual("2 of 3 envoys still stand. Hold off the Drudges for 6:05.", objective.ProgressText);
        }

        [TestMethod]
        public void OutcomeTemplates_SupportTheFamilyToken()
        {
            var goal = BuildHoldGoal();

            var success = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, goal, "Holtburg", WorldEventMvp.None,
                null, 3, successTemplate: "{Family} were driven from {anchor}!", noun: "envoy", nounPlural: "envoys",
                family: "the Emberwrought");

            StringAssert.Contains(success, "The Emberwrought were driven from Holtburg!");

            var fail = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedObjectiveLost, goal, "Holtburg", WorldEventMvp.None,
                null, 3, failTemplate: "{family} took the {nounPlural} - {reason}.", noun: "envoy", nounPlural: "envoys",
                family: "the Blackwing and the Drudges");

            StringAssert.Contains(fail, "the Blackwing and the Drudges took the envoys - the envoys were destroyed.");
            AssertAscii(success);
            AssertAscii(fail);
        }

        [TestMethod]
        public void ProtectedSpeechLine_GlobalCopyFormat()
        {
            var line = WorldEventAnnouncer.ProtectedSpeechLine("Royal Knight", "We made it!");

            StringAssert.EndsWith(line, "Royal Knight: \"We made it!\"");
            AssertAscii(line);

            StringAssert.EndsWith(WorldEventAnnouncer.ProtectedSpeechLine(null, "Help!"), "Ward: \"Help!\"");
        }

        // ---- protect.spawnRadius ------------------------------------------------------------------------

        private static SourceThemeDef SourceWithRadius(float radius, SourceGeometry geometry = SourceGeometry.Edges)
        {
            return new SourceThemeDef
            {
                Id = "ambush", GeometryKind = geometry, GeometryRadius = radius, GeometryPoints = 3
            };
        }

        private static GoalDef HoldGoalWithSpawnRadius(double spawnRadius)
        {
            var protect = BuildProtect();
            protect.SpawnRadius = spawnRadius;

            return BuildHoldGoal(protect: protect);
        }

        [TestMethod]
        public void ResolveSpawnRadius_HoldSpawnRadiusReplacesTheSources()
        {
            Assert.AreEqual(25f, WorldEvent.ResolveSpawnRadius(SourceWithRadius(45f), HoldGoalWithSpawnRadius(25)));
        }

        [TestMethod]
        public void ResolveSpawnRadius_UnsetOrInvalidKeepsTheSources()
        {
            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(SourceWithRadius(45f), HoldGoalWithSpawnRadius(0)));
            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(SourceWithRadius(45f), HoldGoalWithSpawnRadius(double.NaN)));
            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(SourceWithRadius(45f), HoldGoalWithSpawnRadius(-3)));
            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(SourceWithRadius(45f), null));
        }

        [TestMethod]
        public void ResolveSpawnRadius_NonHoldGoalsIgnoreIt()
        {
            var killCount = new GoalDef { Id = "kill_count", TypeKind = GoalType.KillCount, Protect = new ProtectDef { SpawnRadius = 10 } };

            Assert.AreEqual(45f, WorldEvent.ResolveSpawnRadius(SourceWithRadius(45f), killCount),
                "only a Hold goal may move the spawn ring");
        }

        private static readonly Position OutdoorAnchor = new Position(0xA9B40019, 95f, 12f, 94f, 0f, 0f, 0f, 1f, 0);

        [TestMethod]
        public void WaveAnchors_DiscUsesTheGivenRadius()
        {
            var disc = SourceWithRadius(45f, SourceGeometry.Disc);
            var rng = new Random(11);

            for (var i = 0; i < 20; i++)
            {
                foreach (var p in WorldEvent.ResolveWaveAnchors(disc, OutdoorAnchor, new List<Position>(), rng, 25f))
                    Assert.IsTrue(OutdoorAnchor.Distance2D(p) <= 25.01f, $"a disc sample must sit within 25 m, got {OutdoorAnchor.Distance2D(p):F2}");
            }

            // The original overload still uses the source's own radius.
            var staged = new List<Position> { OutdoorAnchor };
            Assert.AreSame(staged, WorldEvent.ResolveWaveAnchors(SourceWithRadius(45f), OutdoorAnchor, staged, rng),
                "a non-disc theme keeps its staged anchors");
        }

        [TestMethod]
        public void GoalsJson_SpawnRadius_ParsesAndValidates()
        {
            var withRadius = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""spawnRadius"": 25.0,");
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(withRadius), null, null);

            Assert.AreEqual(25.0, store.Goals["hold_wards"].Protect.SpawnRadius, 1e-9);

            var negative = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""spawnRadius"": -1,");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(negative), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.spawnRadius")));
            AssertAscii(store.Diagnostics.First(d => d.Contains("protect.spawnRadius")));

            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ValidProtectJson), null, null);
            Assert.AreEqual(0.0, store.Goals["hold_wards"].Protect.SpawnRadius, "absent means keep the source's radius");
        }

        // ---- indoor wave spawns -------------------------------------------------------------------------

        [TestMethod]
        public void RefusesIndoorSpawn_OnlyHoldWavesOutdoorsLandingIndoors()
        {
            Assert.IsTrue(WorldEventHoldRules.RefusesIndoorSpawn(isHoldRun: true, isWave: true, candidateIndoors: true, anchorIndoors: false));

            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(true, true, candidateIndoors: false, anchorIndoors: false), "outdoors is fine");
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(true, true, true, anchorIndoors: true), "an indoor event spawns indoors on purpose");
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(true, isWave: false, true, false), "targets, bosses and decor are not waves");
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(isHoldRun: false, true, true, false), "non-Hold runs are unchanged");
        }

        [TestMethod]
        public void IndoorCellIds_AreRecognised()
        {
            Assert.IsTrue(new Position(0xA9B40143, 140.8f, 5.9f, 94f, 0f, 0f, 0f, 1f, 0).Indoors, "0x0143 is a building interior cell");
            Assert.IsFalse(OutdoorAnchor.Indoors);
        }

        [TestMethod]
        public void AtBearing_PlacesOnTheRingFacingTheCentre()
        {
            foreach (var bearing in new[] { 0.0, Math.PI / 2, Math.PI, 4.0 })
            {
                var p = WorldEventGeometry.AtBearing(OutdoorAnchor, 25f, bearing);

                Assert.AreEqual(25.0, OutdoorAnchor.Distance2D(p), 0.05, $"bearing {bearing}");
                Assert.IsFalse(p.Indoors, "a ring point is addressed as an outdoor cell before the terrain snap");
            }

            Assert.IsNull(WorldEventGeometry.AtBearing(null, 25f, 0));
        }

        // ---- waking the wave ---------------------------------------------------------------------------

        [TestMethod]
        public void NearestWithin_PicksTheClosestInRange()
        {
            Assert.AreEqual(1, WorldEventHoldRules.NearestWithin(new List<double> { 400, 100, 900 }, 1000));
            Assert.AreEqual(0, WorldEventHoldRules.NearestWithin(new List<double> { 100, 100 }, 1000), "ties keep the first");
            Assert.AreEqual(-1, WorldEventHoldRules.NearestWithin(new List<double> { 5000, 9000 }, 1000), "everyone too far");
            Assert.AreEqual(-1, WorldEventHoldRules.NearestWithin(new List<double>(), 1000));
            Assert.AreEqual(-1, WorldEventHoldRules.NearestWithin(null, 1000));
            Assert.AreEqual(1, WorldEventHoldRules.NearestWithin(new List<double> { double.NaN, 50 }, 1000));
        }

        // ---- progress frozen at the end -----------------------------------------------------------------

        [TestMethod]
        public void FreezesProgressAtFinish_HoldOnly()
        {
            Assert.IsTrue(WorldEvent.FreezesProgressAtFinish(BuildHoldGoal()));
            Assert.IsFalse(WorldEvent.FreezesProgressAtFinish(new GoalDef { TypeKind = GoalType.KillCount }));
            Assert.IsFalse(WorldEvent.FreezesProgressAtFinish(new GoalDef { TypeKind = GoalType.DestroySource }));
            Assert.IsFalse(WorldEvent.FreezesProgressAtFinish(null));
        }

        [TestMethod]
        public void Run_ProgressAfterCleanup_ShowsTheFinalCount()
        {
            var (evt, harness) = BuildLiveHoldRun(holdSeconds: 120, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            evt.Tick(T0 + 120);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);

            // Cleanup removes the targets from the world: the live set empties.
            harness.Live.Clear();

            StringAssert.StartsWith(evt.ProgressText, "3 of 3 wards stand.",
                "status after the run must report the count at the finish, not the empty field after cleanup");
        }

        [TestMethod]
        public void Run_ProgressAfterAFail_ShowsTheFinalCount()
        {
            var (evt, harness) = BuildLiveHoldRun(holdSeconds: 420, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            harness.Kill(1);
            harness.Kill(2);
            harness.Kill(3);
            evt.Tick(T0 + 30);

            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, evt.Outcome);
            StringAssert.StartsWith(evt.ProgressText, "0 of 3 wards stand.");
        }

        // ---- --abandon ------------------------------------------------------------------------------------

        [TestMethod]
        public void StartArgs_Abandon_ParsesSeconds()
        {
            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--goal", "defend_envoys", "--abandon", "0" }, out var parsed, out var error), error);
            Assert.AreEqual(0, parsed.AbandonAfterSeconds);

            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "random", "--here", "--abandon", "600" }, out parsed, out error), error);
            Assert.AreEqual(600, parsed.AbandonAfterSeconds, "start random takes it too");

            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--goal", "defend_envoys" }, out parsed, out error), error);
            Assert.IsNull(parsed.AbandonAfterSeconds, "absent means the run default");
        }

        [TestMethod]
        public void StartArgs_Abandon_RejectsBadValues()
        {
            foreach (var bad in new[] { "-1", "abc", "3601", "1.5" })
            {
                Assert.IsFalse(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                    new[] { "--here", "--abandon", bad }, out _, out var error), bad);
                StringAssert.Contains(error, "--abandon");
                AssertAscii(error);
            }

            Assert.IsFalse(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--abandon" }, out _, out var missing), "a value is required");
            Assert.IsNotNull(missing);
        }

        [TestMethod]
        public void Run_AbandonZero_NeverFailsForAnEmptyField()
        {
            var goal = BuildHoldGoal(holdSeconds: 420);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);

            // Nobody counts as present (a solo staff tester) and nothing ever dies.
            evt = BuildHoldEvent(goal, harness.Objective, 600, abandonAfterSeconds: 0, presenceCounter: () => 0);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            evt.Tick(T0 + 200);
            Assert.AreEqual(WorldEventState.Active, evt.State, "--abandon 0 turns the empty-field rule off");

            evt.Tick(T0 + 420);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        [TestMethod]
        public void Run_DefaultAbandon_StillFailsAnEmptyField()
        {
            var goal = BuildHoldGoal(holdSeconds: 420);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);

            evt = BuildHoldEvent(goal, harness.Objective, 600, presenceCounter: () => 0);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            evt.Tick(T0 + WorldEvent.DefaultAbandonAfterSeconds);

            Assert.AreEqual(WorldEventOutcome.FailedNoParticipants, evt.Outcome, "the default is unchanged");
            StringAssert.StartsWith(evt.ProgressText, "3 of 3 wards stand.", "and the final count is the real one");
        }

        // ---- envoys are threatened: hits sized to protect.undefendedSeconds ------------------------------

        [TestMethod]
        public void HitInterval_FirstHitDefault_GapsCapped()
        {
            Assert.AreEqual(WorldEventHoldRules.DefaultHitIntervalSeconds, WorldEventHoldRules.HitInterval(null, T0));
            Assert.AreEqual(1.5, WorldEventHoldRules.HitInterval(T0, T0 + 1.5), 1e-9);
            Assert.AreEqual(WorldEventHoldRules.MaxHitIntervalSeconds, WorldEventHoldRules.HitInterval(T0, T0 + 60),
                "a long gap does not bank into one huge hit");
            Assert.AreEqual(0.0, WorldEventHoldRules.HitInterval(T0 + 5, T0), "clock skew never goes negative");
            Assert.AreEqual(WorldEventHoldRules.DefaultHitIntervalSeconds, WorldEventHoldRules.HitInterval(double.NaN, T0));
        }

        [TestMethod]
        public void ProtectedHitDamage_IsASliceOfMaxHealthPerSecond()
        {
            Assert.AreEqual(640f, WorldEventHoldRules.ProtectedHitDamage(24000, 2.0, 75.0), "24000 * 2 / 75");
            Assert.AreEqual(1f, WorldEventHoldRules.ProtectedHitDamage(10, 0.01, 75.0), "at least 1 for any real hit");
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(24000, 0, 75.0), "same-instant repeat shares the slice");
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(24000, 2.0, 0), "0 means unscaled; the caller keeps the raw hit");
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(0, 2.0, 75.0));
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(24000, double.NaN, 75.0));
        }

        /// <summary>Drives the two pure rules the way a ward's TakeDamage does, for attackers hitting on a fixed swing timer.</summary>
        private static double SecondsToDestroy(uint maxHealth, double undefendedSeconds, int attackers, double swingSeconds)
        {
            var health = (double)maxHealth;
            var last = new double?[attackers];

            for (var t = 0.0; t < 10_000; t += swingSeconds)
            {
                for (var a = 0; a < attackers; a++)
                {
                    var interval = WorldEventHoldRules.HitInterval(last[a], T0 + t);
                    last[a] = T0 + t;

                    health -= WorldEventHoldRules.ProtectedHitDamage(maxHealth, interval, undefendedSeconds);

                    if (health <= 0)
                        return t;
                }
            }

            return double.PositiveInfinity;
        }

        [TestMethod]
        public void OneAttacker_DestroysAnUndefendedEnvoyInAboutUndefendedSeconds()
        {
            foreach (var swing in new[] { 1.5, 2.0, 3.0 })
            {
                var seconds = SecondsToDestroy(24000, 75, attackers: 1, swingSeconds: swing);

                Assert.IsTrue(seconds >= 60 && seconds <= 90,
                    $"one attacker swinging every {swing}s should take 60-90s, took {seconds:F1}s");
            }

            // Health does not change the time: the hits are slices of whatever the max is.
            Assert.AreEqual(SecondsToDestroy(24000, 75, 1, 2.0), SecondsToDestroy(200000, 75, 1, 2.0), 2.0);
        }

        [TestMethod]
        public void MoreAttackers_DestroyItFaster_SoKillingSeekersBuysTime()
        {
            var one = SecondsToDestroy(24000, 75, 1, 2.0);
            var three = SecondsToDestroy(24000, 75, 3, 2.0);

            Assert.IsTrue(three < one / 2.5, $"three attackers ({three:F1}s) should be about three times faster than one ({one:F1}s)");
        }

        private static uint nextHoldGuid = 0x7EE00000;

        private static Creature BuildHoldCreature(int maxHealth = 24000)
        {
            TestGameTables.EnsureInitialized();

            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 2000002,
                WeenieType = ACE.Entity.Enum.WeenieType.Creature,
                PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int>
                {
                    { ACE.Entity.Enum.Properties.PropertyInt.Level, 15 }
                },
                PropertiesAttribute = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute, ACE.Entity.Models.PropertiesAttribute>
                {
                    { ACE.Entity.Enum.Properties.PropertyAttribute.Endurance, new ACE.Entity.Models.PropertiesAttribute { InitLevel = 10 } }
                },
                PropertiesAttribute2nd = new Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute2nd, ACE.Entity.Models.PropertiesAttribute2nd>
                {
                    { ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth, new ACE.Entity.Models.PropertiesAttribute2nd { InitLevel = (uint)maxHealth } }
                }
            };

            var creature = new Creature(weenie, new ObjectGuid(nextHoldGuid++));
            creature.Location = new Position(0x00010064, 50f, 50f, 0f, 0f, 0f, 0f, 1f, 0);

            return creature;
        }

        private static (WorldEvent Evt, Creature Ward, Creature Monster) BuildHitScene(double undefendedSeconds)
        {
            var protect = BuildProtect();
            protect.UndefendedSeconds = undefendedSeconds;

            var evt = BuildHoldEvent(BuildHoldGoal(protect: protect), null);

            var ward = BuildHoldCreature();
            ward.WorldEventProtected = true;
            ward.P_WorldEvent = evt;

            var monster = BuildHoldCreature();
            monster.P_WorldEvent = evt;

            return (evt, ward, monster);
        }

        [TestMethod]
        public void ScaleHit_RunMonsterOnWard_IsASliceOfTheWardsHealth()
        {
            var (_, ward, monster) = BuildHitScene(75);

            var scaled = ward.ScaleWorldEventProtectedHit(monster, 12f);

            var expected = WorldEventHoldRules.ProtectedHitDamage(ward.Health.MaxValue, WorldEventHoldRules.DefaultHitIntervalSeconds, 75);

            Assert.AreEqual(expected, scaled, "a 12-point tusker hit becomes the time-based slice");
            Assert.IsTrue(scaled > 12f, "the weapon's own damage no longer decides it");
        }

        [TestMethod]
        public void ScaleHit_LeavesEveryOtherHitAlone()
        {
            var (_, ward, monster) = BuildHitScene(75);

            Assert.AreEqual(12f, ward.ScaleWorldEventProtectedHit(null, 12f), "a DoT tick with no source");

            var outsider = BuildHoldCreature();
            Assert.AreEqual(12f, ward.ScaleWorldEventProtectedHit(outsider, 12f), "a monster that is not from this run");

            var otherRun = BuildHoldCreature();
            otherRun.P_WorldEvent = BuildHoldEvent(BuildHoldGoal(), null);
            Assert.AreEqual(12f, ward.ScaleWorldEventProtectedHit(otherRun, 12f), "a monster from another run");

            Assert.AreEqual(12f, monster.ScaleWorldEventProtectedHit(ward, 12f), "a creature that is not protected");
            Assert.AreEqual(-5f, ward.ScaleWorldEventProtectedHit(monster, -5f), "a heal is never touched");

            var (_, unscaledWard, unscaledMonster) = BuildHitScene(0);
            Assert.AreEqual(12f, unscaledWard.ScaleWorldEventProtectedHit(unscaledMonster, 12f), "undefendedSeconds 0 keeps raw damage");
        }

        [TestMethod]
        public void GoalsJson_UndefendedSeconds_ParsesAndValidates()
        {
            var json = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""undefendedSeconds"": 75.0,");
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json), null, null);

            Assert.AreEqual(75.0, store.Goals["hold_wards"].Protect.UndefendedSeconds, 1e-9);

            var bad = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""undefendedSeconds"": -10,");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(bad), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.undefendedSeconds")));
            AssertAscii(store.Diagnostics.First(d => d.Contains("protect.undefendedSeconds")));

            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(ValidProtectJson), null, null);
            Assert.AreEqual(0.0, store.Goals["hold_wards"].Protect.UndefendedSeconds, "absent means raw damage");
        }

        // ---- seekers stay locked ------------------------------------------------------------------------

        [TestMethod]
        public void SeekerLock_DoesNothingForANonSeeker()
        {
            var (_, ward, monster) = BuildHitScene(75);
            var player = BuildHoldCreature();

            monster.AttackTarget = player;

            Assert.IsFalse(monster.TryKeepWorldEventSeekerLock(player), "a non-seeker goes for whoever disturbed it, as before");
            Assert.AreSame(player, monster.AttackTarget);
            Assert.IsNotNull(ward);
        }

        [TestMethod]
        public void SeekerLock_FallsBackWhenNoEnvoyIsInReach()
        {
            var (_, _, monster) = BuildHitScene(75);
            monster.WorldEventSeeker = true;

            // Not in the world (no physics, run not Active): there is no envoy to hold it, so the caller's
            // normal retarget runs and the monster is not left with no target.
            Assert.IsFalse(monster.TryKeepWorldEventSeekerLock(null));
            Assert.IsFalse(monster.WorldEventSeeker && monster.AttackTarget is Creature c && c.WorldEventProtected);
        }

        // ---- @smite all ---------------------------------------------------------------------------------

        [TestMethod]
        public void SmiteAll_SkipsProtectedTargets()
        {
            Assert.IsTrue(ACE.Server.Command.Handlers.AdminCommands.SmiteAllTakes(attackable: true, worldEventProtected: false));
            Assert.IsFalse(ACE.Server.Command.Handlers.AdminCommands.SmiteAllTakes(attackable: true, worldEventProtected: true),
                "a envoy survives @smite all");
            Assert.IsFalse(ACE.Server.Command.Handlers.AdminCommands.SmiteAllTakes(attackable: false, worldEventProtected: false),
                "non-attackable creatures were never smitten");
        }

        // ---- the shipped envoy goal ---------------------------------------------------------------------

        /// <summary>A full sample protect block (no health: damage is time-based).</summary>
        private const string SampleProtectJson =
            @"""protect"": { ""wcid"": 2000002, ""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 3 },
                ""radius"": 6.0, ""spawnRadius"": 25.0, ""undefendedSeconds"": 120.0, ""minDeathSeconds"": 45.0,
                ""minSurvivors"": 1, ""seekShare"": 0.8, ""healthMilestones"": true,
                ""noun"": ""envoy"", ""nounPlural"": ""envoys"" }";

        [TestMethod]
        public void EnvoyGoal_ShippedValues_PrioritiseTheEnvoys()
        {
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(SampleProtectJson), null, null);

            Assert.AreEqual(0, store.Diagnostics.Count, "the shipped block loads clean: " + string.Join(" | ", store.Diagnostics));

            var protect = store.Goals["hold_wards"].Protect;

            Assert.AreEqual(0.8, protect.SeekShare, 1e-9);
            Assert.AreEqual(120.0, protect.UndefendedSeconds, 1e-9);
            Assert.AreEqual(45.0, protect.MinDeathSeconds, 1e-9);
            Assert.IsNull(protect.Health, "no dead health setting is shipped");
            Assert.AreEqual(ProtectDef.TimeScaledMaxHealth, protect.EffectiveMaxHealth(20));

            var carry = 0.0;
            var seekers = 0;

            for (var wave = 0; wave < 50; wave++)
                seekers += WorldEventHoldRules.SeekerCount(5, protect.SeekShare, ref carry);

            Assert.IsTrue(seekers >= 199 && seekers <= 200, $"80% of 250 monsters go for the envoys, got {seekers}");
        }

        // ---- --level ------------------------------------------------------------------------------------

        [TestMethod]
        public void ApplyTestLevel_SoloStaffCountsAsOnePlayerOfThatLevel()
        {
            var estimate = WorldEvent.ApplyTestLevel(new AudienceEstimate(0, 0, 0), 150);

            Assert.AreEqual(1, estimate.Count);
            Assert.AreEqual(150, estimate.MedianLevel);
            Assert.AreEqual(150, estimate.P90Level);
            CollectionAssert.AreEqual(new[] { 150 }, estimate.Levels.ToArray(), "the per-slot bands sample this list");
        }

        [TestMethod]
        public void ApplyTestLevel_KeepsTheHeadcountButReplacesTheLevels()
        {
            var real = new AudienceEstimate(4, 30, 40, new List<int> { 20, 30, 35, 40 });

            var estimate = WorldEvent.ApplyTestLevel(real, 275);

            Assert.AreEqual(4, estimate.Count, "wave sizes still follow how many players are there");
            Assert.AreEqual(275, estimate.MedianLevel);
            Assert.IsTrue(estimate.Levels.All(l => l == 275));
            Assert.IsTrue(estimate.PowerSum > real.PowerSum, "boss sizing sees the test level too");
        }

        [TestMethod]
        public void ApplyTestLevel_NullLeavesTheSampleAlone()
        {
            var real = new AudienceEstimate(2, 80, 90, new List<int> { 70, 90 });
            var same = WorldEvent.ApplyTestLevel(real, null);

            Assert.AreEqual(real.Count, same.Count);
            Assert.AreEqual(real.MedianLevel, same.MedianLevel);
            Assert.AreSame(real.Levels, same.Levels);
        }

        [TestMethod]
        public void Run_TestLevel_IsWhatTheRunScalesFrom()
        {
            // A solo admin: the real sample is empty.
            var evt = BuildHoldEvent(BuildHoldGoal(), new HoldHarness(BuildHoldGoal()).Objective,
                testLevel: 150, audience: () => new AudienceEstimate(0, 0, 0));

            evt.Stage(T0);

            Assert.AreEqual(150, evt.Audience.MedianLevel, "the run's audience is the test level, not the level-50 floor");
            Assert.AreEqual(1, evt.Audience.Count);
        }

        [TestMethod]
        public void StartArgs_Level_ParsesAndRejects()
        {
            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--goal", "defend_envoys", "--level", "150" }, out var parsed, out var error), error);
            Assert.AreEqual(150, parsed.TestLevel);

            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "random", "--here", "--level", "275" }, out parsed, out error), error);
            Assert.AreEqual(275, parsed.TestLevel);

            Assert.IsTrue(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                new[] { "--here" }, out parsed, out error), error);
            Assert.IsNull(parsed.TestLevel, "absent means the real players decide");

            foreach (var bad in new[] { "0", "276", "-5", "abc" })
            {
                Assert.IsFalse(ACE.Server.Command.Handlers.WorldEventCommands.TryParseStartArgs(
                    new[] { "--here", "--level", bad }, out _, out error), bad);
                StringAssert.Contains(error, "--level");
                AssertAscii(error);
            }
        }

        // ---- the envoy damage cap ---------------------------------------------------------------------

        /// <summary>Like SecondsToDestroy, through the cap as well (one bucket for the one target).</summary>
        private static double SecondsToDestroyCapped(uint maxHealth, double undefendedSeconds, double minDeathSeconds,
            int attackers, double swingSeconds)
        {
            var health = (double)maxHealth;
            var last = new double?[attackers];
            double tokens = 0, lastAt = 0;

            for (var t = 0.0; t < 10_000; t += swingSeconds)
            {
                for (var a = 0; a < attackers; a++)
                {
                    var now = T0 + t;
                    var interval = WorldEventHoldRules.HitInterval(last[a], now);
                    last[a] = now;

                    var hit = WorldEventHoldRules.ProtectedHitDamage(maxHealth, interval, undefendedSeconds);
                    health -= WorldEventHoldRules.CapProtectedHit(hit, maxHealth, minDeathSeconds, ref tokens, ref lastAt, now);

                    if (health <= 0)
                        return t;
                }
            }

            return double.PositiveInfinity;
        }

        [TestMethod]
        public void Cap_StackedAttackersCannotBeatTheMinimumDeathTime()
        {
            foreach (var attackers in new[] { 3, 5, 10, 25 })
            {
                var seconds = SecondsToDestroyCapped(ProtectDef.TimeScaledMaxHealth, 120, 45, attackers, 2.0);

                Assert.IsTrue(seconds >= 40 && seconds <= 50,
                    $"{attackers} attackers should still need about 45s, took {seconds:F1}s");
            }
        }

        [TestMethod]
        public void Cap_OneOrTwoAttackersAreUnaffected_SoKillingSeekersBuysTime()
        {
            var one = SecondsToDestroyCapped(ProtectDef.TimeScaledMaxHealth, 120, 45, 1, 2.0);
            var two = SecondsToDestroyCapped(ProtectDef.TimeScaledMaxHealth, 120, 45, 2, 2.0);

            Assert.IsTrue(one >= 110 && one <= 125, $"one attacker: about 120s, took {one:F1}s");
            Assert.IsTrue(two >= 55 && two <= 65, $"two attackers: about 60s, took {two:F1}s");
        }

        [TestMethod]
        public void Cap_AnUndefendedEnvoyStillDies()
        {
            Assert.IsTrue(double.IsFinite(SecondsToDestroyCapped(ProtectDef.TimeScaledMaxHealth, 120, 45, 50, 1.0)),
                "the cap slows it down, it never makes a envoy immortal");
        }

        [TestMethod]
        public void CapProtectedHit_ZeroMeansNoCap_AndTheBucketStartsFull()
        {
            double tokens = 0, lastAt = 0;

            Assert.AreEqual(5000f, WorldEventHoldRules.CapProtectedHit(5000f, 10000, 0, ref tokens, ref lastAt, T0), "no cap");

            // Full bucket = 2 s of the capped rate = 10000 / 45 * 2.
            var first = WorldEventHoldRules.CapProtectedHit(5000f, 10000, 45, ref tokens, ref lastAt, T0);
            Assert.AreEqual(10000.0 / 45 * WorldEventHoldRules.DefaultCapBurstSeconds, first, 0.01);

            Assert.AreEqual(0f, WorldEventHoldRules.CapProtectedHit(100f, 10000, 45, ref tokens, ref lastAt, T0),
                "an empty bucket lets nothing through in the same instant");

            var later = WorldEventHoldRules.CapProtectedHit(100f, 10000, 45, ref tokens, ref lastAt, T0 + 1);
            Assert.AreEqual(100f, later, 0.01, "one second later about 222 has refilled");
        }

        [TestMethod]
        public void ScaleHit_RealEnvoy_IsCappedAcrossManyAttackers()
        {
            var protect = BuildProtect();
            protect.Health = null;
            protect.UndefendedSeconds = 120;
            protect.MinDeathSeconds = 45;

            var evt = BuildHoldEvent(BuildHoldGoal(protect: protect), null);

            var ward = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            ward.WorldEventProtected = true;
            ward.P_WorldEvent = evt;

            var max = ward.Health.MaxValue;
            var lost = 0.0;

            // Ten monsters hitting every 2 s for 10 s.
            for (var t = 0; t <= 10; t += 2)
            {
                for (var m = 0; m < 10; m++)
                {
                    var monster = attackersPool[m] ??= NewRunMonster(evt);
                    lost += ward.ScaleWorldEventProtectedHit(monster, 30f, T0 + t);
                }
            }

            var capAllows = max / 45.0 * (10 + WorldEventHoldRules.DefaultCapBurstSeconds);

            Assert.IsTrue(lost <= capAllows + 1, $"lost {lost:F0} in 10 s, the cap allows {capAllows:F0}");
            Assert.IsTrue(lost >= capAllows * 0.9, "and the cap is the only limit: the attackers had plenty to give");
        }

        private readonly Creature[] attackersPool = new Creature[10];

        private static Creature NewRunMonster(WorldEvent evt)
        {
            var monster = BuildHoldCreature();
            monster.P_WorldEvent = evt;
            return monster;
        }

        [TestMethod]
        public void GoalsJson_MinDeathSeconds_Validates()
        {
            var bad = SampleProtectJson.Replace(@"""minDeathSeconds"": 45.0", @"""minDeathSeconds"": -1");
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(bad), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.minDeathSeconds")));
            AssertAscii(store.Diagnostics.First(d => d.Contains("protect.minDeathSeconds")));
        }

        // ---- seekers spread out -------------------------------------------------------------------------

        [TestMethod]
        public void PickLeastAttacked_FewestFirst_NearestOnTies()
        {
            Assert.AreEqual(2, WorldEventHoldRules.PickLeastAttacked(new[] { 2, 1, 0 }, new[] { 100.0, 200.0, 900.0 }, 10000),
                "the empty envoy wins even though it is furthest");
            Assert.AreEqual(1, WorldEventHoldRules.PickLeastAttacked(new[] { 1, 1, 1 }, new[] { 400.0, 100.0, 900.0 }, 10000),
                "a tie goes to the nearest");
            Assert.AreEqual(0, WorldEventHoldRules.PickLeastAttacked(new[] { 3, 0 }, new[] { 100.0, 99999.0 }, 10000),
                "a envoy out of reach is never picked");
            Assert.AreEqual(-1, WorldEventHoldRules.PickLeastAttacked(new int[0], new double[0], 10000));
            Assert.AreEqual(-1, WorldEventHoldRules.PickLeastAttacked(null, null, 10000));
        }

        [TestMethod]
        public void Seekers_PickingOneAfterAnother_SpreadEvenly()
        {
            var attackers = new[] { 0, 0, 0 };
            var distances = new[] { 100.0, 400.0, 900.0 };

            for (var seeker = 0; seeker < 9; seeker++)
                attackers[WorldEventHoldRules.PickLeastAttacked(attackers, distances, 10000)]++;

            CollectionAssert.AreEqual(new[] { 3, 3, 3 }, attackers, "nine seekers, three envoys: three each, not nine on the nearest");
        }

        // ---- protect.health: only for raw damage --------------------------------------------------------

        [TestMethod]
        public void Health_IsIgnoredWhenDamageIsTimeBased()
        {
            var timeBased = BuildProtect();
            timeBased.UndefendedSeconds = 120;

            Assert.IsTrue(timeBased.IsTimeScaled);
            Assert.AreEqual(ProtectDef.TimeScaledMaxHealth, timeBased.EffectiveMaxHealth(1));
            Assert.AreEqual(ProtectDef.TimeScaledMaxHealth, timeBased.EffectiveMaxHealth(50), "players no longer change it");

            var raw = BuildProtect();
            Assert.IsFalse(raw.IsTimeScaled);
            Assert.AreEqual(raw.ResolveHealth(10), raw.EffectiveMaxHealth(10), "raw-damage goals still use protect.health");
        }

        [TestMethod]
        public void GoalsJson_HealthRequiredOnlyForRawDamage_AndFlaggedWhenDead()
        {
            // Time-based without health: fine, nothing to say.
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(SampleProtectJson), null, null);
            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"));
            Assert.AreEqual(0, store.Diagnostics.Count);

            // Time-based WITH health: kept, but the dead setting is called out.
            var withHealth = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0,");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(withHealth), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"));
            var note = store.Diagnostics.FirstOrDefault(d => d.Contains("protect.health") && d.Contains("ignored"));
            Assert.IsNotNull(note, string.Join(" | ", store.Diagnostics));
            AssertAscii(note);

            // Raw damage WITHOUT health: still refused, health is what decides those.
            var rawNoHealth = SampleProtectJson.Replace(@"""undefendedSeconds"": 120.0, ", "");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(rawNoHealth), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.health")));
        }

        // ---- why the test runs ended early: envoy deaths are not credit -----------------------------------

        [TestMethod]
        public void DeathCountsAsCredit_NotForAEnvoy()
        {
            Assert.IsTrue(WorldEvent.DeathCountsAsCredit(isProtectedTarget: false), "a monster the players killed");
            Assert.IsFalse(WorldEvent.DeathCountsAsCredit(isProtectedTarget: true), "a envoy the monsters killed");
        }

        [TestMethod]
        public void WipeGraceFor_AbandonCoversTheWipeTimerToo()
        {
            Assert.AreEqual(0, ACE.Server.Command.Handlers.WorldEventCommands.WipeGraceFor(0), "--abandon 0 also turns the wipe rule off");
            Assert.AreEqual(300, ACE.Server.Command.Handlers.WorldEventCommands.WipeGraceFor(300));
            Assert.AreEqual(WorldEvent.DefaultWipeGraceSeconds, ACE.Server.Command.Handlers.WorldEventCommands.WipeGraceFor(null),
                "without the flag the wipe rule is unchanged");
        }

        [TestMethod]
        public void Run_SoloStaffTest_WithAbandonZero_RunsTheFullHold()
        {
            // The reported run: a staff tester (never counted as present or alive), a kill early on (credit),
            // --abandon 0. With the wipe timer left at 60 s this ended as FailedWipe after about a minute.
            var goal = BuildHoldGoal(holdSeconds: 420);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);

            var request = new WorldEventRequest
            {
                SourceId = "ambush", FamilyId = "emberwrought", GoalId = goal.Id, AnnounceLeadSeconds = 0,
                MaxDurationSeconds = 600, MinDurationSeconds = 300,
                AbandonAfterSeconds = 0, WipeGraceSeconds = ACE.Server.Command.Handlers.WorldEventCommands.WipeGraceFor(0),
                Invoker = "test"
            };

            evt = new WorldEvent(1, BuildHoldEvent(goal, null).Composition, request, harness.Objective,
                () => new AudienceEstimate(0, 0, 0), () => T0, presenceCounter: () => 0);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            evt.LastCreditAt = T0 + 5;     // the tester killed something
            harness.Kill(2);               // and the monsters took one envoy

            evt.Tick(T0 + 70);
            Assert.AreEqual(WorldEventState.Active, evt.State, "no wipe a minute in");

            evt.Tick(T0 + 420);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome, "the full 7 minutes, then success with 2 of 3 standing");
            StringAssert.StartsWith(evt.ProgressText, "2 of 3 wards stand.");
        }

        [TestMethod]
        public void Run_DefaultWipeRule_StillFailsAPlayerlessFieldAfterACredit()
        {
            // The wipe rule itself is unchanged for real runs: credit, then nobody alive for 60 s.
            var goal = BuildHoldGoal(holdSeconds: 420);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);

            evt = BuildHoldEvent(goal, harness.Objective, presenceCounter: () => 0, abandonAfterSeconds: 0);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);
            evt.LastCreditAt = T0 + 5;

            evt.Tick(T0 + WorldEvent.DefaultWipeGraceSeconds + 1);

            Assert.AreEqual(WorldEventOutcome.FailedWipe, evt.Outcome);
        }

        // ---- a successful hold is obvious ----------------------------------------------------------------

        [TestMethod]
        public void HoldOutcomeTemplate_FillsTheFinalCount()
        {
            // Only the count tokens are filled here; {nounPlural} and {anchor} are left for the announcer.
            Assert.AreEqual("The {nounPlural} at {anchor} made it through! 2 of 3 {nounPlural} survived.",
                WorldEvent.HoldOutcomeTemplate("The {nounPlural} at {anchor} made it through! {alive} of {total} {nounPlural} survived.", true, 2, 3));

            Assert.AreEqual("x 1 lost", WorldEvent.HoldOutcomeTemplate("x {lost} lost", false, 2, 3));
            Assert.AreEqual("{alive}", WorldEvent.HoldOutcomeTemplate("{alive}", true, null, null), "not a Hold run: untouched");

            var fallback = WorldEvent.HoldOutcomeTemplate(null, success: true, 3, 3);
            StringAssert.Contains(fallback, "3 of 3 {nounPlural} survived");
            AssertAscii(fallback);

            Assert.IsNull(WorldEvent.HoldOutcomeTemplate(null, success: false, 1, 3), "a fail with no template keeps the standard fail line");
        }

        [TestMethod]
        public void Run_Success_BroadcastsTheFinalCount()
        {
            var saved = WorldEventAnnouncer.BroadcastSink;
            var lines = new List<string>();

            try
            {
                WorldEventAnnouncer.BroadcastSink = lines.Add;

                var goal = BuildHoldGoal(holdSeconds: 120);
                goal.SuccessTemplate = "The {nounPlural} at {anchor} made it through! {alive} of {total} {nounPlural} survived.";

                WorldEvent evt = null;
                var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
                evt = BuildHoldEvent(goal, harness.Objective);

                evt.Stage(T0);
                evt.Tick(T0);
                harness.Place(1, 2, 3);
                harness.Kill(3);

                evt.Tick(T0 + 120);

                Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);

                var outcome = lines.FirstOrDefault(l => l.Contains("made it through"));
                Assert.IsNotNull(outcome, "a clear success broadcast: " + string.Join(" | ", lines));
                StringAssert.Contains(outcome, "2 of 3 wards survived.");
                AssertAscii(outcome);
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = saved;
            }
        }

        // ---- knight variety ---------------------------------------------------------------------------

        [TestMethod]
        public void WcidFor_CyclesThroughTheList_ElseUsesWcid()
        {
            var protect = new ProtectDef { Wcid = 2000002, Wcids = new List<uint> { 2000002, 2000003, 2000004 } };

            CollectionAssert.AreEqual(new uint[] { 2000002, 2000003, 2000004, 2000002 },
                Enumerable.Range(0, 4).Select(protect.WcidFor).ToArray());

            Assert.AreEqual(2000002u, new ProtectDef { Wcid = 2000002 }.WcidFor(5), "no list: every target is wcid");
            Assert.AreEqual(2000003u, new ProtectDef { Wcids = new List<uint> { 2000003 } }.WcidFor(0), "a list alone is enough");
        }

        [TestMethod]
        public void GoalsJson_Wcids_ParseAndValidate()
        {
            var json = SampleProtectJson.Replace(@"""wcid"": 2000002,", @"""wcids"": [ 2000002, 2000003, 2000004 ],");
            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(2000004u, store.Goals["hold_wards"].Protect.WcidFor(2));

            var withZero = SampleProtectJson.Replace(@"""wcid"": 2000002,", @"""wcids"": [ 2000002, 0 ],");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(withZero), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.wcids")));
        }

        // ---- the envoy scene ----------------------------------------------------------------------------

        [TestMethod]
        public void PlaceTargetDecor_PutsAPropBehindEveryTarget()
        {
            var ring = WorldEventHoldRules.RingOffsets(3, 6.0);
            var post = new DecorDef { Wcid = 28527, Dx = 0, Dy = -2.8f, Scale = 1, Speed = 1, Yaw = 90, Snap = true };

            var placed = WorldEventHoldRules.PlaceTargetDecor(ring, new List<DecorDef> { post });

            Assert.AreEqual(3, placed.Count, "one post per knight");

            for (var i = 0; i < 3; i++)
            {
                var t = ring[i];
                var w = placed[i];

                // 2.8 m behind the knight means 3.2 m from the anchor, on the same bearing.
                Assert.AreEqual(3.2, Math.Sqrt(w.Dx * w.Dx + w.Dy * w.Dy), 1e-3);
                Assert.AreEqual(Math.Atan2(t.Dx, t.Dy), Math.Atan2(w.Dx, w.Dy), 1e-3, "on the knight's own bearing");
                Assert.AreEqual(90f + t.YawDegrees, w.Yaw, 1e-3f, "turned with the knight");
                Assert.AreEqual(28527u, w.Wcid);
                Assert.IsTrue(w.Snap);
            }

            Assert.AreEqual(1f, post.Scale, "the goal's own entry is not modified");
            Assert.AreEqual(-2.8f, post.Dy);
        }

        [TestMethod]
        public void PlaceTargetDecor_RightIsToTheTargetsRight()
        {
            // A single target on the anchor faces north, so its right is east.
            var placed = WorldEventHoldRules.PlaceTargetDecor(WorldEventHoldRules.RingOffsets(1, 0),
                new List<DecorDef> { new DecorDef { Wcid = 147, Dx = 1.5f, Dy = 0, Scale = 1, Speed = 1 } });

            Assert.AreEqual(1.5f, placed[0].Dx, 1e-4f);
            Assert.AreEqual(0f, placed[0].Dy, 1e-4f);
            Assert.AreEqual(0, WorldEventHoldRules.PlaceTargetDecor(null, null).Count);
        }

        [TestMethod]
        public void GoalsJson_SceneDecor_BadEntriesDropped_GoalKept()
        {
            var json = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""decor"": [ { ""wcid"": 4128, ""scale"": 1.0, ""speed"": 1.0, ""snap"": true }, { ""wcid"": 0, ""scale"": 1.0, ""speed"": 1.0 } ],
                  ""targetDecor"": [ { ""wcid"": 28527, ""dy"": -2.8, ""scale"": 1.0, ""speed"": 1.0, ""yaw"": 90.0, ""snap"": true }, { ""wcid"": 147, ""scale"": 0, ""speed"": 1.0 } ],
                  ""radius"": 6.0,");

            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), "a bad prop never drops the goal");

            var protect = store.Goals["hold_wards"].Protect;
            Assert.AreEqual(1, protect.Decor.Count);
            Assert.AreEqual(1, protect.TargetDecor.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.decor entry 1")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.targetDecor entry 1")));
        }

        // ---- name colour ----------------------------------------------------------------------------------

        [TestMethod]
        public void ProtectedAppearance_IsAFriendlyNpcToTheClient()
        {
            var knight = BuildHoldCreature();
            knight.Attackable = true;
            knight.PlayerKillerStatus = ACE.Entity.Enum.PlayerKillerStatus.Creature;

            ACE.Server.WorldEvents.WorldEventSpawner.ApplyProtectedAppearance(knight);

            Assert.IsFalse(knight.Attackable, "not attackable: the client shows a friendly name and no attack");
            Assert.AreEqual(ACE.Entity.Enum.PlayerKillerStatus.RubberGlue, knight.PlayerKillerStatus, "the retail shopkeeper status");
            Assert.IsFalse(knight.IsMonster, "nothing keyed on IsMonster treats it as a monster");
        }

        [TestMethod]
        public void ProtectedAppearance_RunMonstersStillDamageIt_PlayersStillCannot()
        {
            var (_, ward, monster) = BuildHitScene(120);

            ACE.Server.WorldEvents.WorldEventSpawner.ApplyProtectedAppearance(ward);

            Assert.IsTrue(monster.IsWorldEventProtectedTargetFor(ward), "its own run's monsters still reach it");
            Assert.IsTrue(monster.CanDamage(ward), "and still damage it (spells use CanDamage)");
            Assert.IsTrue(ward.ScaleWorldEventProtectedHit(monster, 10f, T0) > 0f, "and their hits still land");

            var outsider = BuildHoldCreature();
            Assert.IsFalse(outsider.CanDamage(ward), "a monster from outside the event cannot");
        }

        // ---- wave-based hold ------------------------------------------------------------------------------

        private static GoalDef WaveHoldGoal(int holdSeconds = 480, double minSeconds = 300, int wavesBase = 4,
            double wavesPerParticipant = 0.2, int wavesCap = 10)
        {
            var protect = BuildProtect();
            protect.Waves = new ScaledCount { Base = wavesBase, PerParticipant = wavesPerParticipant, Cap = wavesCap };
            protect.MinSeconds = minSeconds;

            return BuildHoldGoal(holdSeconds: holdSeconds, protect: protect);
        }

        [TestMethod]
        public void Waves_TargetScalesWithTheCrowd_UpwardsOnly()
        {
            var h = new HoldHarness(WaveHoldGoal());

            Assert.AreEqual(4, h.Objective.TargetWaves, "base 4 before any sample");

            h.Objective.OnAudienceResampled(10);
            Assert.AreEqual(6, h.Objective.TargetWaves, "4 + ceil(0.2 * 10)");

            h.Objective.OnAudienceResampled(3);
            Assert.AreEqual(6, h.Objective.TargetWaves, "never lowered when players leave");

            h.Objective.OnAudienceResampled(500);
            Assert.AreEqual(10, h.Objective.TargetWaves, "capped");
        }

        [TestMethod]
        public void WavesWon_NeedsEveryWaveStartedAndCleared_NoMinimumTime()
        {
            var done = new HoldWaveCounts(started: 7, landed: 7, cleared: 7, live: 0);

            Assert.IsTrue(HoldObjective.WavesWon(done, 7), "the finale is cleared: won, however early");
            Assert.IsFalse(HoldObjective.WavesWon(new HoldWaveCounts(6, 6, 6, 0), 7), "a wave still to come");
            Assert.IsFalse(HoldObjective.WavesWon(new HoldWaveCounts(7, 7, 6, 3), 7), "the last wave is still standing");
            Assert.IsFalse(HoldObjective.WavesWon(new HoldWaveCounts(7, 7, 7, 1), 7), "a straggler left");
            Assert.IsFalse(HoldObjective.WavesWon(new HoldWaveCounts(7, 0, 0, 0), 7), "nothing ever landed");
            Assert.IsTrue(HoldObjective.WavesWon(new HoldWaveCounts(10, 10, 10, 0), 40), "a target above the cap is won at the cap");
        }

        [TestMethod]
        public void WantsAnotherWave_NeverPastTheTotal_AndPacedByMinSeconds()
        {
            Assert.IsTrue(HoldObjective.WantsAnotherWave(started: 3, target: 7, elapsed: 400, minSeconds: 180), "waves still to come");
            Assert.IsFalse(HoldObjective.WantsAnotherWave(7, 7, 10, 180), "the in-game bug: all waves out early must NOT add more");
            Assert.IsFalse(HoldObjective.WantsAnotherWave(7, 7, 900, 180));
            Assert.IsFalse(HoldObjective.WantsAnotherWave(9, 7, 320, 180));
            Assert.IsFalse(HoldObjective.WantsAnotherWave(10, 40, 0, 0), "never above 10, whatever the target says");

            // Five waves over 180 s: wave n waits until (n - 1) / 4 of it.
            Assert.IsTrue(HoldObjective.WantsAnotherWave(0, 5, 0, 180), "wave 1 at once");
            Assert.IsFalse(HoldObjective.WantsAnotherWave(1, 5, 44, 180), "wave 2 waits for 45 s");
            Assert.IsTrue(HoldObjective.WantsAnotherWave(1, 5, 45, 180));
            Assert.IsFalse(HoldObjective.WantsAnotherWave(4, 5, 179, 180), "the finale waits for minSeconds");
            Assert.IsTrue(HoldObjective.WantsAnotherWave(4, 5, 180, 180));
        }

        [TestMethod]
        public void EarliestWaveStart_SpreadsTheWavesEvenly()
        {
            CollectionAssert.AreEqual(new[] { 0.0, 45.0, 90.0, 135.0, 180.0 },
                Enumerable.Range(1, 5).Select(n => HoldObjective.EarliestWaveStart(n, 5, 180)).ToArray());

            Assert.AreEqual(0, HoldObjective.EarliestWaveStart(1, 1, 180), "a one-wave hold starts at once");
            Assert.AreEqual(0, HoldObjective.EarliestWaveStart(3, 5, 0), "minSeconds 0: no pacing");
            Assert.AreEqual(180, HoldObjective.EarliestWaveStart(9, 5, 180), 1e-9, "past the total reads as the last");
        }

        [TestMethod]
        public void SimulatedFastSoloRun_NeverExceedsTheTotal()
        {
            // The in-game run: every wave cleared within seconds, a wave asked for every second. It went to 32.
            var target = 5;
            var started = 0;

            for (var second = 0; second <= 480; second++)
            {
                if (HoldObjective.WantsAnotherWave(started, target, second, 180))
                    started++;
            }

            Assert.AreEqual(5, started, "exactly the total, the finale last");
        }

        [TestMethod]
        public void Waves_TargetNeverAboveTen_AndFrozenOnceTheFinaleStarts()
        {
            var h = new HoldHarness(WaveHoldGoal(wavesBase: 40, wavesPerParticipant: 0, wavesCap: 40));
            Assert.AreEqual(ProtectDef.MaxHoldWaves, h.Objective.TargetWaves, "a goal asking for 40 gets 10");

            var g = new HoldHarness(WaveHoldGoal(wavesBase: 4, wavesPerParticipant: 1, wavesCap: 10));
            Assert.AreEqual(4, g.Objective.TargetWaves);

            g.Waves = new HoldWaveCounts(4, 4, 3, 2);   // the finale is out
            g.Objective.OnAudienceResampled(6);
            Assert.AreEqual(4, g.Objective.TargetWaves, "more players arriving during the finale add nothing after it");
            Assert.IsTrue(g.Objective.IsFinalWave(4));
        }

        [TestMethod]
        public void MinSeconds_DefaultIs180()
        {
            Assert.AreEqual(180, new ProtectDef().MinSeconds, 1e-9);
        }

        [TestMethod]
        public void GoalsJson_WavesAboveTheCap_AreNoted()
        {
            var json = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 11.0, ""waves"": { ""base"": 4, ""perParticipant"": 1, ""cap"": 30 }, ""minSeconds"": 180.0,");

            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("never has more than 10 waves")), string.Join(" | ", store.Diagnostics));
        }

        [TestMethod]
        public void Run_WaveHold_SucceedsTheMomentTheLastWaveIsCleared()
        {
            var goal = WaveHoldGoal(holdSeconds: 480, minSeconds: 300, wavesBase: 4, wavesPerParticipant: 0, wavesCap: 4);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
            evt = BuildHoldEvent(goal, harness.Objective, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            harness.Waves = new HoldWaveCounts(4, 4, 3, 2);
            evt.Tick(T0 + 310);
            Assert.AreEqual(WorldEventState.Active, evt.State, "the final wave is still fighting");

            harness.Waves = new HoldWaveCounts(4, 4, 4, 0);
            evt.Tick(T0 + 311);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome, "success on the tick its last monster is gone");
        }

        [TestMethod]
        public void Run_WaveHold_FinaleClearedEarly_SucceedsAtOnce()
        {
            var goal = WaveHoldGoal(holdSeconds: 480, minSeconds: 180, wavesBase: 4, wavesPerParticipant: 0, wavesCap: 4);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
            evt = BuildHoldEvent(goal, harness.Objective, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);

            harness.Waves = new HoldWaveCounts(4, 4, 4, 0);
            Assert.IsFalse(harness.Objective.WantsAnotherWave(T0 + 60), "no wave past the total");

            evt.Tick(T0 + 60);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome, "finale cleared at 60 s: success, minSeconds or not");
        }

        [TestMethod]
        public void Run_WaveHold_HoldSecondsEndsIt_EvenWithWavesStillToCome()
        {
            var goal = WaveHoldGoal(holdSeconds: 480, minSeconds: 180, wavesBase: 6, wavesPerParticipant: 0, wavesCap: 6);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
            evt = BuildHoldEvent(goal, harness.Objective, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);
            harness.Kill(2);
            harness.Waves = new HoldWaveCounts(3, 3, 2, 4);   // a slow group, waves 4-6 never started

            evt.Tick(T0 + 479);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            evt.Tick(T0 + 480);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome, "holdSeconds ends it; 2 of 3 standing is enough");
        }

        [TestMethod]
        public void ProgressText_NeverShowsAWaveAboveTheTotal()
        {
            var h = new HoldHarness(WaveHoldGoal(wavesBase: 5, wavesPerParticipant: 0, wavesCap: 5));
            h.Place(1, 2);

            foreach (var started in new[] { 5, 6, 32 })
            {
                h.Waves = new HoldWaveCounts(started, started, started - 1, 1);
                h.Objective.Tick(T0 + 10);

                StringAssert.Contains(h.Objective.ProgressText, "Wave 5 of 5", $"started={started}");
                StringAssert.Contains(h.Objective.WaveStartText(started), "Wave 5 of 5", $"wave start line, started={started}");
            }
        }

        [TestMethod]
        public void Run_WaveHold_HoldSecondsIsTheSafetyCap()
        {
            var goal = WaveHoldGoal(holdSeconds: 480, minSeconds: 300, wavesBase: 4, wavesPerParticipant: 0, wavesCap: 4);
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
            evt = BuildHoldEvent(goal, harness.Objective, maxDurationSeconds: 600);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2, 3);
            harness.Waves = new HoldWaveCounts(4, 4, 3, 5);   // the last wave never dies

            evt.Tick(T0 + 479);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            evt.Tick(T0 + 480);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome, "holdSeconds ends it with the envoys still standing");
        }

        [TestMethod]
        public void Run_WaveHold_StillFailsWhenTheTargetsFall()
        {
            var goal = WaveHoldGoal();
            WorldEvent evt = null;
            var harness = new HoldHarness(goal, () => evt?.ActiveAt ?? 0);
            evt = BuildHoldEvent(goal, harness.Objective);

            evt.Stage(T0);
            evt.Tick(T0);
            harness.Place(1, 2);
            harness.Waves = new HoldWaveCounts(9, 9, 9, 0);
            harness.Kill(1);
            harness.Kill(2);

            evt.Tick(T0 + 400);

            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, evt.Outcome, "waves won with no envoys left is still a loss");
        }

        [TestMethod]
        public void WaveProgress_AndTheWaveStartLine()
        {
            Assert.AreEqual("Wave 3 of 7", WorldEventHoldRules.WaveProgress(3, 7));
            Assert.AreEqual("Wave 0 of 7", WorldEventHoldRules.WaveProgress(0, 7));
            Assert.AreEqual("Wave 7 of 7", WorldEventHoldRules.WaveProgress(8, 7), "never above the total");

            var goal = WaveHoldGoal(wavesBase: 7, wavesPerParticipant: 0, wavesCap: 7);
            goal.Protect.Noun = "envoy";
            goal.Protect.NounPlural = "envoys";

            var h = new HoldHarness(goal);
            h.Place(1, 2, 3);

            Assert.AreEqual("Wave 3 of 7 - 3 of 3 envoys stand.", h.Objective.WaveStartText(3));
            AssertAscii(h.Objective.WaveStartText(3));
            AssertAscii(h.Objective.WaveStartText(9));
        }

        [TestMethod]
        public void WaveHold_DefaultProgressShowsTheWave()
        {
            var goal = WaveHoldGoal(wavesBase: 7, wavesPerParticipant: 0, wavesCap: 7);
            goal.ProgressTemplate = null;

            var h = new HoldHarness(goal);
            h.Place(1, 2, 3);
            h.Waves = new HoldWaveCounts(3, 3, 2, 4);
            h.Objective.Tick(T0 + 60);

            Assert.AreEqual("Wave 3 of 7 - 3 of 3 wards stand.", h.Objective.ProgressText);
        }

        [TestMethod]
        public void FinalWave_IsTheCountedLastOne_AndALittleBigger()
        {
            var h = new HoldHarness(WaveHoldGoal(wavesBase: 7, wavesPerParticipant: 0, wavesCap: 7));

            Assert.IsFalse(h.Objective.IsFinalWave(6));
            Assert.IsTrue(h.Objective.IsFinalWave(7));
            Assert.IsFalse(h.Objective.IsFinalWave(8), "an extra wave after the count is not the final one");
            Assert.IsFalse(new HoldHarness(BuildHoldGoal()).Objective.IsFinalWave(1), "a time-only hold has no final wave");

            Assert.AreEqual(1, WorldEventHoldRules.FinalWaveBonus(4), "a quarter more, at least one");
            Assert.AreEqual(2, WorldEventHoldRules.FinalWaveBonus(6));
            Assert.AreEqual(1, WorldEventHoldRules.FinalWaveBonus(0));
        }

        [TestMethod]
        public void TimeOnlyHold_IsUnchangedWithoutWaves()
        {
            var h = new HoldHarness(BuildHoldGoal(holdSeconds: 420));
            h.Place(1);
            h.Waves = new HoldWaveCounts(50, 50, 50, 0);

            h.Objective.Tick(T0 + 419);
            Assert.IsFalse(h.Objective.IsComplete, "waves mean nothing to a goal without protect.waves");
            Assert.IsTrue(h.Objective.WantsAnotherWave(T0 + 419));

            h.Objective.Tick(T0 + 420);
            Assert.IsTrue(h.Objective.IsComplete);
        }

        [TestMethod]
        public void GoalsJson_WaveSettings_ParseAndValidate()
        {
            var json = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 11.0, ""waves"": { ""base"": 4, ""perParticipant"": 0.2, ""cap"": 10 }, ""minSeconds"": 300.0,
                  ""healBetweenWaves"": 0.15, ""maxHealPerTenSeconds"": 0.10,");

            var store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(json, holdSeconds: 480), null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            var p = store.Goals["hold_wards"].Protect;
            Assert.IsTrue(p.IsWaveBased);
            Assert.AreEqual(10, p.Waves.Cap);
            Assert.AreEqual(300.0, p.MinSeconds, 1e-9);
            Assert.AreEqual(0.15, p.HealBetweenWaves, 1e-9);
            Assert.AreEqual(0.10, p.MaxHealPerTenSeconds, 1e-9);

            foreach (var (bad, key) in new[]
            {
                (@"""waves"": { ""base"": 0, ""perParticipant"": 0, ""cap"": 4 },", "protect.waves"),
                (@"""minSeconds"": -1,", "protect.minSeconds"),
                (@"""healBetweenWaves"": 1.5,", "protect.healBetweenWaves"),
                (@"""maxHealPerTenSeconds"": -0.1,", "protect.maxHealPerTenSeconds")
            })
            {
                var badJson = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + bad);
                store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(badJson), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), bad);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(key)), key);
            }

            // minSeconds above the safety cap can never be used.
            var tooLong = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""waves"": { ""base"": 4, ""perParticipant"": 0, ""cap"": 4 }, ""minSeconds"": 600,");
            store = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(tooLong, holdSeconds: 480), null, null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.minSeconds") && d.Contains("holdSeconds")));
        }

        // ---- healing between waves -----------------------------------------------------------------------

        [TestMethod]
        public void BetweenWavesHeal_IsTheFractionOfMaxHealth()
        {
            Assert.AreEqual(1500, WorldEventHoldRules.BetweenWavesHeal(10000, 0.15));
            Assert.AreEqual(10000, WorldEventHoldRules.BetweenWavesHeal(10000, 5.0), "never more than full");
            Assert.AreEqual(0, WorldEventHoldRules.BetweenWavesHeal(10000, 0), "0 heals nothing");
            Assert.AreEqual(0, WorldEventHoldRules.BetweenWavesHeal(0, 0.15));
        }

        // ---- player healing -----------------------------------------------------------------------------

        [TestMethod]
        public void ConvertPlayerHeal_ATypicalHealIsFourPercent_CappedPerTenSeconds()
        {
            double tokens = 0, lastAt = 0;

            Assert.AreEqual(400, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.10, ref tokens, ref lastAt, T0),
                "one typical heal (100) restores 4% of 10000");
            Assert.AreEqual(600, WorldEventHoldRules.ConvertPlayerHeal(300, 10000, 0.10, ref tokens, ref lastAt, T0),
                "a bigger heal is worth more, but only 600 of the 1000 per ten seconds is left");
            Assert.AreEqual(0, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.10, ref tokens, ref lastAt, T0),
                "the cap is spent");
            Assert.AreEqual(100, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0.10, ref tokens, ref lastAt, T0 + 1),
                "it refills at 100 per second (10% of 10000 per ten seconds)");
        }

        [TestMethod]
        public void ConvertPlayerHeal_ManyHealersCannotBeatTheCap()
        {
            double tokens = 0, lastAt = 0;
            var healed = 0;

            // Ten healers each landing a 200-point heal every second for 30 seconds.
            for (var t = 0; t < 30; t++)
                for (var healer = 0; healer < 10; healer++)
                    healed += WorldEventHoldRules.ConvertPlayerHeal(200, 10000, 0.10, ref tokens, ref lastAt, T0 + t);

            // A full first window plus 29 seconds of refill: about 1000 + 2900.
            Assert.IsTrue(healed <= 3900 + 10, $"healed {healed}, more than the cap allows");
            Assert.IsTrue(healed >= 3800, $"healed {healed}, the cap should be the only limit");
        }

        [TestMethod]
        public void ConvertPlayerHeal_OffWhenTheSettingIsZero()
        {
            double tokens = 0, lastAt = 0;

            Assert.AreEqual(0, WorldEventHoldRules.ConvertPlayerHeal(100, 10000, 0, ref tokens, ref lastAt, T0));
            Assert.AreEqual(0, WorldEventHoldRules.ConvertPlayerHeal(-50, 10000, 0.1, ref tokens, ref lastAt, T0), "never a negative heal");
        }

        [TestMethod]
        public void TakeFromBucket_Guards()
        {
            double tokens = 0, lastAt = 0;

            Assert.AreEqual(0, WorldEventHoldRules.TakeFromBucket(double.NaN, 10, 10, ref tokens, ref lastAt, T0));
            Assert.AreEqual(-3, WorldEventHoldRules.TakeFromBucket(-3, 10, 10, ref tokens, ref lastAt, T0), "non-positive passes through");
            Assert.AreEqual(0, WorldEventHoldRules.TakeFromBucket(5, 0, 10, ref tokens, ref lastAt, T0), "no rate, nothing through");
        }

        [TestMethod]
        public void HealSpellRule_OnlyBeneficialHealthBoosts()
        {
            Assert.IsTrue(Creature.IsWorldEventProtectedHealSpell(true, ACE.Entity.Enum.SpellType.Boost, ACE.Entity.Enum.DamageType.Health, 55, 120),
                "Heal Other");
            Assert.IsFalse(Creature.IsWorldEventProtectedHealSpell(false, ACE.Entity.Enum.SpellType.Boost, ACE.Entity.Enum.DamageType.Health, -50, -100),
                "Harm");
            Assert.IsFalse(Creature.IsWorldEventProtectedHealSpell(true, ACE.Entity.Enum.SpellType.Boost, ACE.Entity.Enum.DamageType.Stamina, 55, 120),
                "a stamina boost");
            Assert.IsFalse(Creature.IsWorldEventProtectedHealSpell(true, ACE.Entity.Enum.SpellType.Enchantment, ACE.Entity.Enum.DamageType.Health, 0, 0),
                "a buff");
            Assert.IsFalse(Creature.IsWorldEventProtectedHealSpell(null), "no spell");
        }

        [TestMethod]
        public void ProtectedHeal_RealEnvoy_ConvertsAndCaps()
        {
            var (_, ward, _) = BuildHitScene(120);
            ward.P_WorldEvent.Composition.Goal.Protect.MaxHealPerTenSeconds = 0.10;

            Assert.IsTrue(ward.AcceptsWorldEventPlayerHeal);

            var max = ward.Health.MaxValue;
            var first = ward.ConvertWorldEventProtectedHeal(100, T0);

            Assert.AreEqual((int)Math.Floor(max * 0.04), first, "4% of max for a typical heal");

            var total = first;

            for (var i = 0; i < 20; i++)
                total += ward.ConvertWorldEventProtectedHeal(100, T0);

            Assert.IsTrue(total <= max * 0.10 + 1, $"capped at 10% of max per window, got {total} of {max}");

            var plain = BuildHoldCreature();
            Assert.AreEqual(100, plain.ConvertWorldEventProtectedHeal(100, T0), "any other creature is healed normally");
            Assert.IsFalse(plain.AcceptsWorldEventPlayerHeal);
        }

        [TestMethod]
        public void ProtectedHeal_TurnedOffByTheGoal()
        {
            var (_, ward, _) = BuildHitScene(120);
            ward.P_WorldEvent.Composition.Goal.Protect.MaxHealPerTenSeconds = 0;

            Assert.IsFalse(ward.AcceptsWorldEventPlayerHeal, "spells and kits are refused");
            Assert.AreEqual(0, ward.ConvertWorldEventProtectedHeal(100, T0));
        }

        // ---- less clutter ------------------------------------------------------------------------------------

        [TestMethod]
        public void Scene_OneCartBehindEachTarget_SpreadOnTheWiderRing()
        {
            var ring = WorldEventHoldRules.RingOffsets(3, 11.0);
            var cart = new DecorDef { Wcid = 28527, Dy = -3.5f, Scale = 1, Speed = 1, Yaw = 90, Snap = true };

            var placed = WorldEventHoldRules.PlaceTargetDecor(ring, new List<DecorDef> { cart });

            Assert.AreEqual(3, placed.Count);

            foreach (var p in placed)
                Assert.AreEqual(7.5, Math.Sqrt(p.Dx * p.Dx + p.Dy * p.Dy), 1e-3, "11 m ring, cart 3.5 m back toward the fire");

            // Targets 11 m out are about 19 m apart: the group has to split up.
            var gap = Math.Sqrt(Math.Pow(ring[0].Dx - ring[1].Dx, 2) + Math.Pow(ring[0].Dy - ring[1].Dy, 2));
            Assert.AreEqual(19.05, gap, 0.05);
        }

        // ---- neighbouring landblocks --------------------------------------------------------------------

        [TestMethod]
        public void HoldsAdjacentLandblocks_AlwaysForHold_ThemeDecidesOtherwise()
        {
            var noAdjacents = new SourceThemeDef { HoldAdjacentLandblocks = false };
            var adjacents = new SourceThemeDef { HoldAdjacentLandblocks = true };
            var killCount = new GoalDef { TypeKind = GoalType.KillCount };

            Assert.IsTrue(WorldEvent.HoldsAdjacentLandblocks(noAdjacents, BuildHoldGoal()), "a Hold run near an edge keeps its envoys");
            Assert.IsFalse(WorldEvent.HoldsAdjacentLandblocks(noAdjacents, killCount), "non-Hold runs are unchanged");
            Assert.IsTrue(WorldEvent.HoldsAdjacentLandblocks(adjacents, killCount));
            Assert.IsFalse(WorldEvent.HoldsAdjacentLandblocks(null, null));
        }
    }
}
