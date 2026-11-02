using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H0: the HoldObjective skeleton and its factory gate.
    ///
    /// The success / fail / abort / progress rules are ported from DreamWeave #17's WorldEventHoldTests (and the WavesWon
    /// checks from its WorldEventHoldPhaseTests), driven through the objective's func-fed testable shape: no database, no
    /// landblock, no live Creature (TECH-DESIGN D6).
    ///
    /// The factory tests seed world_events_hold_enabled through PropertyManager.ModifyBool and restore its shipped value in a
    /// finally, so this class never reads the key from a database and never leaks it to a later class.
    /// </summary>
    [TestClass]
    public class WorldEventHoldObjectiveTests
    {
        private const double T0 = 1_700_000_000d;

        private const string HoldEnabledKey = "world_events_hold_enabled";

        // ---- builders (the contributor's shapes) --------------------------------------------------------------------

        private static ProtectDef BuildProtect(int minSurvivors = 1)
        {
            return new ProtectDef
            {
                Wcid = 2000001,
                Count = new ScaledCount { Base = 1, PerParticipant = 0, Cap = 3 },
                Health = new ScaledCount { Base = 20000, PerParticipant = 4000, Cap = 200000 },
                Radius = 6.0,
                MinSurvivors = minSurvivors,
                SeekShare = 0.3,
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

        private static GoalDef WaveHoldGoal(int holdSeconds = 480, double minSeconds = 300, int wavesBase = 4,
            double wavesPerParticipant = 0.2, int wavesCap = 10)
        {
            var protect = BuildProtect();
            protect.Waves = new ScaledCount { Base = wavesBase, PerParticipant = wavesPerParticipant, Cap = wavesCap };
            protect.MinSeconds = minSeconds;

            return BuildHoldGoal(holdSeconds: holdSeconds, protect: protect);
        }

        /// <summary>A HoldObjective over plain lists the test mutates, with a settable Active time.</summary>
        private sealed class HoldHarness
        {
            public readonly List<uint> Placed = new List<uint>();
            public readonly List<uint> Live = new List<uint>();
            public double ActiveAt = T0;
            public readonly HoldObjective Objective;
            public HoldWaveCounts Waves;

            public HoldHarness(GoalDef goal, Func<string> family = null)
            {
                Objective = new HoldObjective(goal, () => Placed, () => Live, () => ActiveAt, null, family, () => Waves);
            }

            public void Place(params uint[] guids)
            {
                Placed.AddRange(guids);
                Live.AddRange(guids);
            }

            public void Kill(uint guid) => Live.Remove(guid);
        }

        // ---- success, fail, abort -----------------------------------------------------------------------------------

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
        public void Hold_FailedObjectiveLost_IsAnObjectiveFailure_AndAppended()
        {
            Assert.IsTrue(WorldEvent.IsObjectiveFailureOutcome(WorldEventOutcome.FailedObjectiveLost));
            Assert.AreEqual((int)WorldEventOutcome.AbortedError + 1, (int)WorldEventOutcome.FailedObjectiveLost,
                "appended, so no existing outcome changes its number");
        }

        [TestMethod]
        public void Hold_NeverStopsTheWaves_AndKeepsTheNormalCadence()
        {
            var h = new HoldHarness(BuildHoldGoal());
            h.Place(1);

            IWorldEventObjective objective = h.Objective;

            Assert.IsNull(objective.RemainingKills, "null is 'no opinion' - 0 would suppress every wave");
            Assert.IsNull(objective.TargetKills);
            Assert.IsFalse(objective.ReplacesWaveCadence, "the skeleton runs no cadence of its own");
            Assert.AreEqual(0.0, objective.PresenceAfkSeconds, "the skeleton leaves the presence idle rule off");
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

        [TestMethod]
        public void Hold_Tick_MarksTheRunActive_AndFinishMarksItInactive()
        {
            var h = new HoldHarness(BuildHoldGoal()) { ActiveAt = 0 };

            h.Objective.Tick(T0);
            Assert.IsFalse(h.Objective.Run.IsActive, "not Active yet");

            h.ActiveAt = T0;
            h.Objective.Tick(T0 + 1);
            Assert.IsTrue(h.Objective.Run.IsActive);

            h.Objective.OnFinish(WorldEventOutcome.Success, T0 + 2);
            Assert.IsFalse(h.Objective.Run.IsActive);
        }

        // ---- waves ----------------------------------------------------------------------------------------------------

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
        public void WavesWon_WaitsForTheFinaleToBePlaced()
        {
            var unplaced = new HoldWaveCounts(started: 5, landed: 4, cleared: 4, live: 0, resolved: 4);
            Assert.IsFalse(HoldObjective.WavesWon(unplaced, 5), "the finale has not landed: not won");

            Assert.IsFalse(HoldObjective.WavesWon(new HoldWaveCounts(5, 5, 4, 7, 5), 5), "the finale is fighting");
            Assert.IsTrue(HoldObjective.WavesWon(new HoldWaveCounts(5, 5, 5, 0, 5), 5), "the finale and its boss are dead");
            Assert.IsTrue(HoldObjective.WavesWon(new HoldWaveCounts(5, 4, 4, 0, 5), 5), "a finale whose placement failed still resolves");
        }

        [TestMethod]
        public void WaveHold_IsCompleteOnceTheWavesAreWon_BeforeHoldSeconds()
        {
            var h = new HoldHarness(WaveHoldGoal(holdSeconds: 480, wavesBase: 4, wavesPerParticipant: 0, wavesCap: 4));
            h.Place(1, 2, 3);

            h.Waves = new HoldWaveCounts(4, 4, 3, 5);
            h.Objective.Tick(T0 + 60);
            Assert.IsFalse(h.Objective.IsComplete, "the last wave still stands");

            h.Waves = new HoldWaveCounts(4, 4, 4, 0);
            Assert.IsTrue(h.Objective.IsComplete, "every wave cleared at 60 s: complete");
        }

        [TestMethod]
        public void WaveHold_HoldSecondsIsTheSafetyCap()
        {
            var h = new HoldHarness(WaveHoldGoal(holdSeconds: 480, wavesBase: 4, wavesPerParticipant: 0, wavesCap: 4));
            h.Place(1, 2, 3);
            h.Waves = new HoldWaveCounts(4, 4, 3, 5);

            h.Objective.Tick(T0 + 479);
            Assert.IsFalse(h.Objective.IsComplete);

            h.Objective.Tick(T0 + 480);
            Assert.IsTrue(h.Objective.IsComplete, "holdSeconds ends it with the wards still standing");
        }

        [TestMethod]
        public void WaveHold_WavesWonWithNoWardsLeft_IsStillALoss()
        {
            var h = new HoldHarness(WaveHoldGoal());
            h.Place(1, 2);
            h.Waves = new HoldWaveCounts(9, 9, 9, 0);
            h.Kill(1);
            h.Kill(2);

            h.Objective.Tick(T0 + 400);

            Assert.IsFalse(h.Objective.IsComplete);
            Assert.AreEqual(WorldEventOutcome.FailedObjectiveLost, h.Objective.FailureOutcome);
        }

        // ---- progress text ------------------------------------------------------------------------------------------

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
        public void ProgressText_SupportsTheFamilyToken()
        {
            var goal = BuildHoldGoal(holdSeconds: 420, progressTemplate: "{alive} of {total} {nounPlural} still stand. Hold off {family} for {remaining}.",
                protect: new ProtectDef
                {
                    Wcid = 2000002, Count = new ScaledCount { Base = 3, Cap = 3 }, Health = new ScaledCount { Base = 1, Cap = 1 },
                    Noun = "envoy", NounPlural = "envoys"
                });

            var h = new HoldHarness(goal, () => "the Drudges");
            h.Place(1, 2, 3);
            h.Kill(2);
            h.Objective.Tick(T0 + 55);

            Assert.AreEqual("2 of 3 envoys still stand. Hold off the Drudges for 6:05.", h.Objective.ProgressText);

            var noFamily = new HoldHarness(goal);
            noFamily.Place(1);
            noFamily.Objective.Tick(T0 + 55);

            StringAssert.Contains(noFamily.Objective.ProgressText, WorldEventHoldRules.DefaultFamilyText);
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
        public void FormatRemaining_IsMinutesAndPaddedSeconds()
        {
            Assert.AreEqual("0:00", HoldObjective.FormatRemaining(0));
            Assert.AreEqual("0:00", HoldObjective.FormatRemaining(-5));
            Assert.AreEqual("1:05", HoldObjective.FormatRemaining(65));
            Assert.AreEqual("7:00", HoldObjective.FormatRemaining(420));
        }

        // ---- the factory gate ---------------------------------------------------------------------------------------

        private static WorldEvent BuildEvent(GoalDef goal)
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
                CompatibleGoals = new List<string> { goal.Id }
            };

            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string>(),
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
                ClaimWindowSeconds = 300
            };

            var anchor = new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() };

            var composition = new WorldEventComposition(source, new[] { family }, BossDef.None, goal, reward, anchor,
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);

            var request = new WorldEventRequest { SourceId = "ambush", FamilyId = "emberwrought", GoalId = goal.Id, Invoker = "test" };

            return new WorldEvent(9, composition, request, null, () => new AudienceEstimate(3, 100, 150), () => T0);
        }

        private static void WithHoldEnabled(bool enabled, Action body)
        {
            PropertyManager.ModifyBool(HoldEnabledKey, enabled);

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyBool(HoldEnabledKey, DefaultPropertyManager.DefaultBooleanProperties[HoldEnabledKey].Item);
            }
        }

        [TestMethod]
        public void Factory_HoldOff_RefusesAndNamesTheProperty()
        {
            WithHoldEnabled(false, () =>
            {
                var objective = WorldEventObjectiveFactory.Create(BuildEvent(BuildHoldGoal()), out var error);

                Assert.IsNull(objective);
                StringAssert.Contains(error, HoldEnabledKey);
                Assert.AreEqual(HoldEnabledKey, WorldEventObjectiveFactory.HoldEnabledProperty);
            });
        }

        [TestMethod]
        public void Factory_HoldOn_BuildsTheSkeleton_OwningARunForThisRunId()
        {
            WithHoldEnabled(true, () =>
            {
                var evt = BuildEvent(BuildHoldGoal());
                var objective = WorldEventObjectiveFactory.Create(evt, out var error);

                Assert.IsNull(error);
                Assert.IsInstanceOfType(objective, typeof(HoldObjective));

                var hold = (HoldObjective)objective;
                Assert.IsNotNull(hold.Run);
                Assert.AreEqual(evt.RunId, hold.Run.RunId);
                Assert.IsFalse(hold.Run.IsActive, "nothing is active before the run is");
                Assert.AreEqual(0, hold.Placed, "the skeleton places no knights");
                Assert.IsFalse(hold.ReplacesWaveCadence);
            });
        }

        [TestMethod]
        public void Factory_HoldOn_WithNoProtectBlock_Refuses()
        {
            WithHoldEnabled(true, () =>
            {
                var goal = BuildHoldGoal();
                goal.Protect = null;

                var objective = WorldEventObjectiveFactory.Create(BuildEvent(goal), out var error);

                Assert.IsNull(objective);
                StringAssert.Contains(error, "protect");
            });
        }

        [TestMethod]
        public void Factory_TheSwitchIsReadAtEveryCreate()
        {
            WithHoldEnabled(true, () => Assert.IsNotNull(WorldEventObjectiveFactory.Create(BuildEvent(BuildHoldGoal()), out _)));
            WithHoldEnabled(false, () => Assert.IsNull(WorldEventObjectiveFactory.Create(BuildEvent(BuildHoldGoal()), out _)));
        }

        // ---- the start-spot seam ------------------------------------------------------------------------------------

        [TestMethod]
        public void StartSpotRule_IsTheNoOpForEveryGoalButHold_AndLeavesTheAnchorWhereItWas()
        {
            foreach (var kind in (GoalType[])Enum.GetValues(typeof(GoalType)))
            {
                var goal = BuildHoldGoal();
                goal.TypeKind = kind;

                var evt = BuildEvent(goal);
                var before = new Position(evt.Composition.AnchorPosition);

                var rule = WorldEventObjectiveFactory.StartSpotRuleFor(goal);

                // H5: a Hold goal gets its own rule (clear area, town reach); this goal declares neither, so it too stands.
                if (kind == GoalType.Hold)
                    Assert.AreSame(ACE.Server.WorldEvents.Hold.WorldEventHoldStartSpotRule.Instance, rule, kind.ToString());
                else
                    Assert.AreSame(WorldEventNoStartSpotRule.Instance, rule, kind.ToString());
                Assert.IsTrue(rule.TryResolve(evt.Composition, null, out var error), kind.ToString());
                Assert.IsNull(error);
                Assert.AreEqual(before.LandblockId.Raw, evt.Composition.AnchorPosition.LandblockId.Raw);
                Assert.AreEqual(before.Pos, evt.Composition.AnchorPosition.Pos);
            }

            Assert.AreSame(WorldEventNoStartSpotRule.Instance, WorldEventObjectiveFactory.StartSpotRuleFor(null));
        }
    }
}
