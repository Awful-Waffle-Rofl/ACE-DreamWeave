using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-02 World Events state machine, timers, composer, catalog builder and
    /// audience estimate. Everything here is a pure static or a plain object: no database, no landblock,
    /// no live Player or Session (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventStateMachineTests
    {
        // ---- fakes and builders ----------------------------------------------------------------------

        private sealed class FakeObjective : IWorldEventObjective
        {
            public bool IsComplete { get; set; }

            public string ProgressText { get; set; } = "0 of 1 slain.";

            public int TickCount;

            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now) => TickCount++;

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private const int TestClaimWindowSeconds = 120;

        private static readonly double T0 = 1_700_000_000d;

        private static SourceThemeDef BuildSource(string id = "ambush", params string[] compatibleGoals)
        {
            return new SourceThemeDef
            {
                Id = id,
                DisplayName = "Ambush",
                Geometry = "edges",
                GeometryKind = SourceGeometry.Edges,
                GeometryRadius = 45f,
                GeometryPoints = 3,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                HoldAdjacentLandblocks = false,
                RewardRadius = 60f,
                CompatibleGoals = (compatibleGoals == null || compatibleGoals.Length == 0
                    ? new[] { "kill_count" }
                    : compatibleGoals).ToList(),
                StartFlavour = "Something is coming through the weave near {anchor}.",
                WaveFlavour = "Another rank tears its way in!"
            };
        }

        private static GoalDef BuildGoal(string id = "kill_count")
        {
            return new GoalDef
            {
                Id = id,
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = ACE.Server.WorldEvents.Defs.MvpRule.MostKills,
                Count = new ScaledCount { Base = 20, PerParticipant = 6, Cap = 150 },
                ProgressTemplate = "{killed} of {target} slain."
            };
        }

        private static RewardDef BuildReward(string id = "standard")
        {
            return new RewardDef
            {
                Id = id,
                DisplayName = "Hammer Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = TestClaimWindowSeconds,
                GateByCharacter = true,
                GateByAccount = true,
                GateByIp = true
            };
        }

        private static FamilyDef BuildFamily(string id = "emberwrought")
        {
            return new FamilyDef
            {
                Id = id,
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string> { "volcanic" },
                Members = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 }
                }
            };
        }

        private static AnchorDef BuildAnchor()
        {
            return new AnchorDef
            {
                Id = "here",
                DisplayName = "the test anchor",
                CellId = 0x016C019E,
                BiomeTags = new List<string>()
            };
        }

        private static WorldEventComposition BuildComposition(GoalDef goal = null)
        {
            return new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, BossDef.None, goal ?? BuildGoal(),
                BuildReward(), BuildAnchor(), new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                WorldEventAxisStore.Empty);
        }

        /// <summary>The destroy_source goal, for the TECH-DESIGN 2.15 hold-open tests.</summary>
        private static GoalDef BuildDestroySourceGoal()
        {
            return new GoalDef
            {
                Id = "destroy_source",
                DisplayName = "Destroy Source",
                Type = "DestroySource",
                TypeKind = GoalType.DestroySource,
                MvpRule = "killingBlowAndTopDamage",
                RuleKind = ACE.Server.WorldEvents.Defs.MvpRule.KillingBlowAndTopDamage,
                ProgressTemplate = "{remaining} of {total} rifts remain."
            };
        }

        private static WorldEvent BuildEvent(FakeObjective objective, int announceLeadSeconds = 0,
            double now = 0, int maxDurationSeconds = WorldEvent.DefaultMaxDurationSeconds,
            int minDurationSeconds = WorldEvent.DefaultMinDurationSeconds,
            int abandonAfterSeconds = WorldEvent.DefaultAbandonAfterSeconds,
            int wipeGraceSeconds = WorldEvent.DefaultWipeGraceSeconds,
            GoalDef goal = null, Func<int> presenceCounter = null)
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = goal?.Id ?? "kill_count",
                AnnounceLeadSeconds = announceLeadSeconds,
                MaxDurationSeconds = maxDurationSeconds,
                MinDurationSeconds = minDurationSeconds,
                AbandonAfterSeconds = abandonAfterSeconds,
                WipeGraceSeconds = wipeGraceSeconds,
                Invoker = "test"
            };

            var clockValue = now == 0 ? T0 : now;

            return new WorldEvent(1, BuildComposition(goal), request, objective,
                () => new AudienceEstimate(3, 100, 150), () => clockValue,
                presenceCounter: presenceCounter);
        }

        private static Weenie BuildCreatureWeenie(uint wcid, string family, int? level, int? role,
            bool worldEventCreature = true, bool attackable = true,
            WeenieType type = WeenieType.Creature, bool objectiveFlag = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"testcreature{wcid}",
                WeenieType = type,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string>()
            };

            if (worldEventCreature)
                weenie.PropertiesBool[PropertyBool.WorldEventCreature] = true;

            if (attackable)
                weenie.PropertiesBool[PropertyBool.Attackable] = true;

            if (objectiveFlag)
                weenie.PropertiesBool[PropertyBool.WorldEventObjective] = true;

            if (family != null)
                weenie.PropertiesString[PropertyString.WorldEventFamily] = family;

            weenie.PropertiesString[PropertyString.Name] = $"Test Creature {wcid}";

            if (level != null)
                weenie.PropertiesInt[PropertyInt.Level] = level.Value;

            if (role != null)
                weenie.PropertiesInt[PropertyInt.WorldEventRole] = role.Value;

            return weenie;
        }

        // ---- (a) transition legality matrix -----------------------------------------------------------

        [TestMethod]
        public void IsLegal_HappyPathEdges_AreAllLegal()
        {
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Idle, WorldEventState.Staged));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Staged, WorldEventState.Announced));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Announced, WorldEventState.Active));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Active, WorldEventState.Resolved));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Resolved, WorldEventState.Rewarding));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Resolved, WorldEventState.Cleanup));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Rewarding, WorldEventState.Cleanup));
            Assert.IsTrue(WorldEventStateMachine.IsLegal(WorldEventState.Cleanup, WorldEventState.Done));
        }

        [TestMethod]
        public void IsLegal_DoneIsTerminal()
        {
            foreach (WorldEventState to in Enum.GetValues(typeof(WorldEventState)))
                Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Done, to), $"Done -> {to} must be refused");
        }

        [TestMethod]
        public void IsLegal_ResolvedIsReachableFromEveryNonDoneState()
        {
            foreach (WorldEventState from in Enum.GetValues(typeof(WorldEventState)))
            {
                if (from == WorldEventState.Done)
                    continue;

                Assert.IsTrue(WorldEventStateMachine.IsLegal(from, WorldEventState.Resolved), $"{from} -> Resolved must be legal");
            }
        }

        [TestMethod]
        public void IsLegal_SkippingStatesIsRefused()
        {
            Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Idle, WorldEventState.Active));
            Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Idle, WorldEventState.Announced));
            Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Staged, WorldEventState.Active));
            Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Active, WorldEventState.Done));
            Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Rewarding, WorldEventState.Done));
            Assert.IsFalse(WorldEventStateMachine.IsLegal(WorldEventState.Active, WorldEventState.Idle));
        }

        // ---- WP-21 review: ResolvedAudienceCount safety --------------------------------------------------

        /// <summary>
        /// Before Stage() runs, Audience is still the AudienceEstimate struct's default (Count 0) -
        /// ResolvedAudienceCount must report that rather than throw, since Audience (WorldEvent.cs:147) is
        /// a non-nullable readonly struct and can never itself be null.
        /// </summary>
        [TestMethod]
        public void ResolvedAudienceCount_BeforeStage_IsZero()
        {
            var evt = BuildEvent(new FakeObjective());

            Assert.AreEqual(WorldEventState.Idle, evt.State);
            Assert.AreEqual(0, evt.ResolvedAudienceCount);
        }

        /// <summary>
        /// Stage() assigns Audience before completing the Announced transition (WorldEvent.cs:351 area), so
        /// by the time a run reaches Announced (or later), ResolvedAudienceCount reports the real sample -
        /// here the audienceSampler BuildEvent wires in (AudienceEstimate(3, ...)).
        /// </summary>
        [TestMethod]
        public void ResolvedAudienceCount_AfterStage_ReturnsTheSampledCount()
        {
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);
            Assert.AreEqual(3, evt.ResolvedAudienceCount);
        }

        // ---- (b) stage and the announce lead ----------------------------------------------------------

        [TestMethod]
        public void Stage_WithZeroLead_ReachesActiveOnTheFirstTick()
        {
            var evt = BuildEvent(new FakeObjective(), announceLeadSeconds: 0);

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);
            Assert.AreEqual(T0, evt.StagedAt);

            evt.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(T0, evt.ActiveAt);
        }

        [TestMethod]
        public void Stage_WithThirtySecondLead_StaysAnnouncedUntilTheLeadExpires()
        {
            var evt = BuildEvent(new FakeObjective(), announceLeadSeconds: 30);

            evt.Stage(T0);

            evt.Tick(T0 + 29);
            Assert.AreEqual(WorldEventState.Announced, evt.State);

            evt.Tick(T0 + 30);
            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(T0 + 30, evt.ActiveAt);
        }

        [TestMethod]
        public void Stage_SamplesTheAudienceOnce()
        {
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);

            Assert.AreEqual(3, evt.Audience.Count);
            Assert.AreEqual(100, evt.Audience.MedianLevel);
            Assert.AreEqual(150, evt.Audience.P90Level);
        }

        // ---- (c) success opens a claim window, then Tick closes the run --------------------------------

        [TestMethod]
        public void Finish_Success_OpensTheClaimWindowAndTickClosesTheRun()
        {
            var objective = new FakeObjective();
            var evt = BuildEvent(objective);

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, evt.State);

            evt.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
            Assert.AreEqual(T0 + TestClaimWindowSeconds, evt.ClaimWindowEndsAt);

            evt.Tick(T0 + TestClaimWindowSeconds - 1);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State);

            evt.Tick(T0 + TestClaimWindowSeconds);
            Assert.AreEqual(WorldEventState.Done, evt.State);
        }

        [TestMethod]
        public void Tick_CompletedObjective_FinishesWithSuccess()
        {
            var objective = new FakeObjective();

            // Minimum duration switched off, so this stays the pre-2.15 assertion: a completed objective
            // ends the run on the next tick. The minimum's own effect is the test below.
            var evt = BuildEvent(objective, minDurationSeconds: 0);

            evt.Stage(T0);
            evt.Tick(T0);

            objective.IsComplete = true;
            evt.Tick(T0 + 1);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        /// <summary>
        /// TECH-DESIGN 2.15: a kill_count run that meets its target early is HELD OPEN until the minimum
        /// duration has passed, and then finishes on the first tick past it. This is the behaviour change
        /// that the test above deliberately opts out of.
        /// </summary>
        [TestMethod]
        public void Tick_CompletedKillCountObjective_IsHeldOpenUntilTheMinimumDuration()
        {
            var objective = new FakeObjective();

            // Abandon and wipe left at their DEFAULTS (90 s and 60 s). Nobody is credited in a unit test,
            // so before TECH-DESIGN 2.15's suppression this run failed as abandoned at T0+90, long before
            // the minimum expired - which is the defect this test now pins shut.
            var evt = BuildEvent(objective, minDurationSeconds: 300);

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.AreEqual(T0, evt.ActiveAt);

            objective.IsComplete = true;

            evt.Tick(T0 + 1);
            Assert.AreEqual(WorldEventState.Active, evt.State, "one second in, the run has 299 seconds still to serve");

            evt.Tick(T0 + 299);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            evt.Tick(T0 + 300);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "the boundary itself releases it");
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        /// <summary>
        /// TECH-DESIGN 2.15, the defect code review caught: with the objective already MET, execution falls
        /// through to the timers, and wipe/abandon would take the run away from players who had already
        /// earned it. Both grace windows are left at their DEFAULTS here and both are ticked well past,
        /// with nobody ever credited - the run must still reach Success at the minimum.
        ///
        /// FailedTimeout is deliberately still armed; it cannot fire first, because ValidateDurations
        /// refuses a minimum at or above the maximum.
        /// </summary>
        [TestMethod]
        public void HeldOpenObjective_IsNotFailedByTheWipeOrAbandonTimers()
        {
            var objective = new FakeObjective();

            var evt = BuildEvent(objective, minDurationSeconds: 300,
                abandonAfterSeconds: WorldEvent.DefaultAbandonAfterSeconds,
                wipeGraceSeconds: WorldEvent.DefaultWipeGraceSeconds);

            evt.Stage(T0);
            evt.Tick(T0);

            objective.IsComplete = true;

            // Past the 60 s wipe grace and the 90 s abandon window, with no credit at any point.
            foreach (var offset in new[] { 61d, 91d, 150d, 299d })
            {
                evt.Tick(T0 + offset);

                Assert.AreEqual(WorldEventState.Active, evt.State,
                    $"a met objective must not be failed at +{offset}s just because the field went quiet");
                Assert.AreEqual(WorldEventOutcome.None, evt.Outcome);
            }

            evt.Tick(T0 + 300);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        /// <summary>
        /// The suppression must NOT become a general immunity: a run whose objective is still open fails on
        /// the abandon rule exactly as it always did.
        /// </summary>
        [TestMethod]
        public void AnIncompleteObjective_StillFailsOnTheAbandonRule()
        {
            var evt = BuildEvent(new FakeObjective(), minDurationSeconds: 300);

            evt.Stage(T0);
            evt.Tick(T0);

            evt.Tick(T0 + 91);

            Assert.AreEqual(WorldEventOutcome.FailedNoParticipants, evt.Outcome,
                "nothing about the minimum duration excuses a run nobody ever fought in");
        }

        /// <summary>
        /// Owner decision 2026-08-16: destroy_source is held by the minimum exactly like kill_count. The
        /// same wipe/abandon suppression covers it, and it too resolves Success at the minimum.
        /// </summary>
        [TestMethod]
        public void DestroySource_CompletedEarly_IsAlsoHeldOpenAndThenSucceeds()
        {
            var objective = new FakeObjective();

            var evt = BuildEvent(objective, minDurationSeconds: 300, goal: BuildDestroySourceGoal());

            evt.Stage(T0);
            evt.Tick(T0);

            objective.IsComplete = true;

            evt.Tick(T0 + 1);
            Assert.AreEqual(WorldEventState.Active, evt.State, "the last rift falling no longer ends the run outright");

            evt.Tick(T0 + 91);
            Assert.AreEqual(WorldEventState.Active, evt.State, "and the abandon rule does not end it either");
            Assert.AreEqual(WorldEventOutcome.None, evt.Outcome);

            evt.Tick(T0 + 299);
            Assert.AreEqual(WorldEventState.Active, evt.State);

            evt.Tick(T0 + 300);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        // ---- (d) an abort skips Rewarding entirely ----------------------------------------------------

        [TestMethod]
        public void Finish_AbortedAdmin_SkipsRewardingAndReachesDoneInOneCall()
        {
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);
            evt.Tick(T0);

            evt.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.AreEqual(WorldEventOutcome.AbortedAdmin, evt.Outcome);
            Assert.AreEqual(0d, evt.ClaimWindowEndsAt);
            Assert.AreEqual(0d, evt.RewardingAt);
        }

        [TestMethod]
        public void Finish_AbortedShutdownBeforeActive_StillReachesDone()
        {
            var evt = BuildEvent(new FakeObjective(), announceLeadSeconds: 30);

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);

            evt.Finish(WorldEventOutcome.AbortedShutdown);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.AreEqual(WorldEventOutcome.AbortedShutdown, evt.Outcome);
        }

        // ---- (e) Finish is idempotent -----------------------------------------------------------------

        [TestMethod]
        public void Finish_CalledTwice_SecondCallIsANoOp()
        {
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);
            evt.Tick(T0);

            evt.Finish(WorldEventOutcome.Success);

            var stateAfterFirst = evt.State;
            var windowAfterFirst = evt.ClaimWindowEndsAt;

            evt.Finish(WorldEventOutcome.Success);
            evt.Finish(WorldEventOutcome.FailedTimeout);

            Assert.AreEqual(stateAfterFirst, evt.State);
            Assert.AreEqual(windowAfterFirst, evt.ClaimWindowEndsAt);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
        }

        [TestMethod]
        public void Finish_AfterDone_IsANoOp()
        {
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);
            evt.Tick(T0);
            evt.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(WorldEventState.Done, evt.State);

            evt.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.AreEqual(WorldEventOutcome.AbortedAdmin, evt.Outcome);
        }

        [TestMethod]
        public void Finish_AbortDuringTheClaimWindow_CutsItShort()
        {
            // A shutdown raised while the claim window is open must still destroy everything the run
            // spawned, so this one repeat call is deliberately NOT a no-op.
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);
            evt.Tick(T0);
            evt.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State);

            evt.Finish(WorldEventOutcome.AbortedShutdown);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.AreEqual(WorldEventOutcome.AbortedShutdown, evt.Outcome);
        }

        // ---- cleanup log line, TECH-DESIGN 5.2 fixed format -------------------------------------------

        /// <summary>
        /// The cleanup line's "survivors" field is fixed by 5.2 as a comma-separated guid list or "none",
        /// and monitoring queries will be written against it. WP-02 has no post-destroy survivor check, so
        /// it must report "none" even when the held list was NOT empty - reporting the scheduled count
        /// there would read as "all N both destroyed and survived".
        ///
        /// A null entry in the held list is the cheapest way to make the list non-empty without
        /// constructing a WorldObject, which D6 forbids; the destroy loop already guards for it.
        /// </summary>
        [TestMethod]
        public void Cleanup_ReportsSurvivorsNoneEvenWhenTheHeldListIsNotEmpty()
        {
            var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)
                log4net.LogManager.GetRepository(typeof(WorldEvent).Assembly);

            var appender = new log4net.Appender.MemoryAppender();
            appender.ActivateOptions();

            var priorLevel = hierarchy.Root.Level;
            var priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = log4net.Core.Level.All;
            hierarchy.Configured = true;

            try
            {
                var evt = BuildEvent(new FakeObjective());

                evt.Stage(T0);
                evt.Tick(T0);

                evt.HeldObjects.Add(null);

                evt.Finish(WorldEventOutcome.AbortedAdmin);

                var cleanupLines = appender.GetEvents()
                    .Select(e => e.RenderedMessage)
                    .Where(m => m != null && m.Contains($"run={evt.RunId} cleanup "))
                    .ToList();

                Assert.AreEqual(1, cleanupLines.Count,
                    "expected exactly one cleanup line, got: " + string.Join(" | ", cleanupLines));

                StringAssert.Contains(cleanupLines[0], "survivors=none");
                StringAssert.Contains(cleanupLines[0], "destroyed=0");
            }
            finally
            {
                hierarchy.Root.RemoveAppender(appender);
                hierarchy.Root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
                appender.Close();
            }
        }

        // ---- (f) run token ----------------------------------------------------------------------------

        [TestMethod]
        public void RunValid_RejectsAStaleTokenAndAnythingAfterDone()
        {
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.IsTrue(evt.RunValid(evt.RunId));
            Assert.IsFalse(evt.RunValid(evt.RunId + 1));
            Assert.IsFalse(evt.RunValid(0));

            evt.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.IsFalse(evt.RunValid(evt.RunId));
        }

        // ---- (g) timer matrix -------------------------------------------------------------------------

        private static WorldEventTimers.TimerInputs Inputs(double elapsed, bool credited = false,
            double sinceLastCredit = 0, double sinceLastAlive = 0, int max = 600, int abandon = 90, int wipe = 60,
            bool present = false)
        {
            var now = T0 + elapsed;

            return new WorldEventTimers.TimerInputs
            {
                Now = now,
                ActiveAt = T0,
                LastCreditAt = credited ? now - sinceLastCredit : 0,
                MaxDurationSeconds = max,
                AbandonAfterSeconds = abandon,
                WipeGraceSeconds = wipe,
                AnyoneEverCredited = credited,
                LastAliveParticipantAt = sinceLastAlive > 0 ? now - sinceLastAlive : 0,
                PlayersPresent = present
            };
        }

        [TestMethod]
        public void Timers_TimeoutFiresAtExactlyMaxNotBefore()
        {
            var justUnder = Inputs(599, credited: true, sinceLastCredit: 1);
            Assert.AreNotEqual(TimerVerdict.FailedTimeout, WorldEventTimers.Evaluate(in justUnder, true, true));

            var exact = Inputs(600, credited: true, sinceLastCredit: 1);
            Assert.AreEqual(TimerVerdict.FailedTimeout, WorldEventTimers.Evaluate(in exact, true, true));
        }

        [TestMethod]
        public void Timers_NeverCredited_AbandonsAtExactlyNinety()
        {
            var justUnder = Inputs(89);
            Assert.AreNotEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in justUnder, false, false));

            var exact = Inputs(90);
            Assert.AreEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in exact, false, false));
        }

        [TestMethod]
        public void Timers_Credited_AbandonsNinetySecondsAfterTheLastCredit()
        {
            var fresh = Inputs(400, credited: true, sinceLastCredit: 89);
            Assert.AreNotEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in fresh, true, true));

            var stale = Inputs(400, credited: true, sinceLastCredit: 90);
            Assert.AreEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in stale, true, true));
        }

        [TestMethod]
        public void Timers_WipeRequiresCreditAndAKnownLastAliveTime()
        {
            // WP-02 always passes LastAliveParticipantAt = 0, so the rule cannot fire yet.
            var noFeed = Inputs(200, credited: true, sinceLastCredit: 1, sinceLastAlive: 0);
            Assert.AreEqual(TimerVerdict.None, WorldEventTimers.Evaluate(in noFeed, true, true));

            var wiped = Inputs(200, credited: true, sinceLastCredit: 1, sinceLastAlive: 60);
            Assert.AreEqual(TimerVerdict.FailedWipe, WorldEventTimers.Evaluate(in wiped, true, true));

            var neverCredited = Inputs(50, credited: false, sinceLastAlive: 60);
            Assert.AreNotEqual(TimerVerdict.FailedWipe, WorldEventTimers.Evaluate(in neverCredited, true, true));
        }

        [TestMethod]
        public void Timers_TimeoutOutranksWipeAndAbandon()
        {
            var everything = Inputs(600, credited: true, sinceLastCredit: 300, sinceLastAlive: 300);
            Assert.AreEqual(TimerVerdict.FailedTimeout, WorldEventTimers.Evaluate(in everything, true, true));

            var wipeAndAbandon = Inputs(400, credited: true, sinceLastCredit: 300, sinceLastAlive: 300);
            Assert.AreEqual(TimerVerdict.FailedWipe, WorldEventTimers.Evaluate(in wipeAndAbandon, true, true));
        }

        [TestMethod]
        public void Timers_WarningsFireOnceEach()
        {
            // 60 s remaining, neither warning spent.
            var at60 = Inputs(540, credited: true, sinceLastCredit: 1);
            Assert.AreEqual(TimerVerdict.Warn60, WorldEventTimers.Evaluate(in at60, false, false));
            Assert.AreEqual(TimerVerdict.None, WorldEventTimers.Evaluate(in at60, true, false));

            // 30 s remaining: the tighter warning wins.
            var at30 = Inputs(570, credited: true, sinceLastCredit: 1);
            Assert.AreEqual(TimerVerdict.Warn30, WorldEventTimers.Evaluate(in at30, true, false));
            Assert.AreEqual(TimerVerdict.None, WorldEventTimers.Evaluate(in at30, true, true));

            // 61 s remaining: nothing yet.
            var at61 = Inputs(539, credited: true, sinceLastCredit: 1);
            Assert.AreEqual(TimerVerdict.None, WorldEventTimers.Evaluate(in at61, false, false));
        }

        [TestMethod]
        public void Timers_NothingFiresBeforeTheEventGoesActive()
        {
            var notActive = new WorldEventTimers.TimerInputs
            {
                Now = T0 + 10_000,
                ActiveAt = 0,
                MaxDurationSeconds = 600,
                AbandonAfterSeconds = 90,
                WipeGraceSeconds = 60
            };

            Assert.AreEqual(TimerVerdict.None, WorldEventTimers.Evaluate(in notActive, false, false));
        }

        [TestMethod]
        public void Timers_AbandonIsHeldOpenWhilePlayersArePresent()
        {
            // Pre-change, this returns FailedNoParticipants at elapsed 120 with no credit - this is the
            // discriminating case: it fails on the old code and passes only because PlayersPresent guards
            // the branch now.
            var stillPresent = Inputs(120, credited: false, present: true);
            Assert.AreNotEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in stillPresent, true, true));
        }

        [TestMethod]
        public void Timers_AbandonStillFiresOnAnEmptyField()
        {
            // Same inputs as above with present: false - proves the guard is a condition, not a blanket
            // disable of the abandon rule.
            var empty = Inputs(120, credited: false, present: false);
            Assert.AreEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in empty, true, true));
        }

        [TestMethod]
        public void Timers_StaleCreditIsHeldOpenWhilePlayersArePresent()
        {
            // Credit went stale 120s ago (past the 90s abandon window), but players are present and the
            // wipe rule is not tripped (sinceLastAlive left at 0, which disables it per WP-02). Pre-change
            // this returns FailedNoParticipants; PlayersPresent is what suppresses it.
            var stalePresent = Inputs(400, credited: true, sinceLastCredit: 120, present: true);
            Assert.AreNotEqual(TimerVerdict.FailedNoParticipants, WorldEventTimers.Evaluate(in stalePresent, true, true));
        }

        [TestMethod]
        public void Timers_TimeoutStillWinsOverPresence()
        {
            // elapsed >= max with present: true must still report FailedTimeout - the absolute backstop is
            // never disarmed by presence. Passes on both old and new code (max check runs before the
            // PlayersPresent-guarded branch), so this pins the precedence rather than discriminating.
            var timedOut = Inputs(600, credited: true, sinceLastCredit: 1, present: true);
            Assert.AreEqual(TimerVerdict.FailedTimeout, WorldEventTimers.Evaluate(in timedOut, true, true));
        }

        [TestMethod]
        public void Timers_PresenceDoesNotSuppressWipeInEvaluate()
        {
            // Presence reaches the wipe rule only through WorldEvent.FeedWipeTimer, never through
            // WorldEventTimers.Evaluate directly - PlayersPresent must have no effect on the wipe branch.
            // Passes on both old and new Evaluate code (PlayersPresent is not referenced by the wipe
            // branch at all), so this pins that isolation rather than discriminating.
            var wipedButPresent = Inputs(400, credited: true, sinceLastCredit: 1, sinceLastAlive: 61, present: true);
            Assert.AreEqual(TimerVerdict.FailedWipe, WorldEventTimers.Evaluate(in wipedButPresent, true, true));
        }

        [TestMethod]
        public void Tick_PresenceSeamHoldsTheRunOpenPastTheAbandonWindow()
        {
            // End-to-end through WorldEvent.Tick via the presenceCounter seam (D6 - no live Player
            // available). Pre-change (no PlayersPresent guard reachable at all) this would resolve
            // FailedNoParticipants regardless of the seam; the seam returning > 0 is what keeps it Active.
            var evt = BuildEvent(new FakeObjective(), abandonAfterSeconds: 90, presenceCounter: () => 2);

            evt.Stage(T0);
            evt.Tick(T0);

            // No credit at all; past the 90s abandon window.
            evt.Tick(T0 + 120);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(WorldEventOutcome.None, evt.Outcome);
        }

        [TestMethod]
        public void Tick_PresenceSeamReturningZeroStillAbandons()
        {
            var evt = BuildEvent(new FakeObjective(), abandonAfterSeconds: 90, presenceCounter: () => 0);

            evt.Stage(T0);
            evt.Tick(T0);

            evt.Tick(T0 + 120);

            Assert.AreEqual(WorldEventOutcome.FailedNoParticipants, evt.Outcome);
        }

        [TestMethod]
        public void Tick_PresenceRescuesTheWipeAfterTheCreditedDefendersAreGone()
        {
            // The discriminator for FeedWipeTimer's presence term. OnCreatureDied(null, null, null) sets
            // LastCreditAt (AnyoneEverCredited = true) without adding any Participation records -
            // CreditDamage/CreditKill both no-op on a null creature/damager (see WorldEventParticipation) -
            // so Participation.Count stays 0 and AliveNear would find nobody even if asked. A fresh group
            // then shows up on the field via presenceCounter, after the original (credited) defenders are
            // gone. This MUST fail on pre-change code: the old FeedWipeTimer returned early whenever
            // Participation.Count == 0, so LastAliveParticipantAt never advanced past its ActiveAt seed and
            // FailedWipe fired once WipeGraceSeconds elapsed. Confirmed red on that old shape - see report.
            var evt = BuildEvent(new FakeObjective(), wipeGraceSeconds: 30, presenceCounter: () => 2);

            evt.Stage(T0);
            evt.Tick(T0);

            evt.OnCreatureDied(null, null, null);

            evt.Tick(T0 + 40);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(WorldEventOutcome.None, evt.Outcome);
        }

        [TestMethod]
        public void Tick_WipeStillFiresWhenNobodyIsPresentEither()
        {
            // The inverse of the test above, NOT a discriminating case: presenceCounter returns 0, so
            // nothing here is any different from the pre-change wipe rule (Participation.Count stays 0
            // either way, so the old early-out and the new presence check both leave LastAliveParticipantAt
            // stuck at ActiveAt). Its only job is to prove the presence term in FeedWipeTimer is a condition
            // - "advance the clock IF someone is present" - rather than a blanket disable of FailedWipe.
            var evt = BuildEvent(new FakeObjective(), wipeGraceSeconds: 30, presenceCounter: () => 0);

            evt.Stage(T0);
            evt.Tick(T0);

            evt.OnCreatureDied(null, null, null);

            evt.Tick(T0 + 40);

            Assert.AreEqual(WorldEventOutcome.FailedWipe, evt.Outcome);
        }

        [TestMethod]
        public void Tick_TimeoutFinishesTheRunWithFailedTimeout()
        {
            var evt = BuildEvent(new FakeObjective(), maxDurationSeconds: 100);

            evt.Stage(T0);
            evt.Tick(T0);

            // Credit something so the abandon rule does not fire first.
            evt.LastCreditAt = T0 + 99;

            evt.Tick(T0 + 100);

            Assert.AreEqual(WorldEventOutcome.FailedTimeout, evt.Outcome);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
        }

        // ---- (h) audience estimate --------------------------------------------------------------------

        [TestMethod]
        public void EstimateFromLevels_EmptyIsAllZero()
        {
            var estimate = WorldEventRosterSelector.EstimateFromLevels(new List<int>());

            Assert.AreEqual(0, estimate.Count);
            Assert.AreEqual(0, estimate.MedianLevel);
            Assert.AreEqual(0, estimate.P90Level);

            var nullEstimate = WorldEventRosterSelector.EstimateFromLevels(null);
            Assert.AreEqual(0, nullEstimate.Count);
        }

        [TestMethod]
        public void EstimateFromLevels_SingleElement()
        {
            var estimate = WorldEventRosterSelector.EstimateFromLevels(new List<int> { 126 });

            Assert.AreEqual(1, estimate.Count);
            Assert.AreEqual(126, estimate.MedianLevel);
            Assert.AreEqual(126, estimate.P90Level);
        }

        [TestMethod]
        public void EstimateFromLevels_OddCountTakesTheMiddle()
        {
            var estimate = WorldEventRosterSelector.EstimateFromLevels(new List<int> { 50, 10, 30 });

            Assert.AreEqual(3, estimate.Count);
            Assert.AreEqual(30, estimate.MedianLevel);

            // ceil(0.9 * 3) - 1 = 2 -> the top element.
            Assert.AreEqual(50, estimate.P90Level);
        }

        [TestMethod]
        public void EstimateFromLevels_EvenCountTakesTheLowerMiddle()
        {
            var estimate = WorldEventRosterSelector.EstimateFromLevels(new List<int> { 10, 20, 30, 40 });

            Assert.AreEqual(4, estimate.Count);
            Assert.AreEqual(20, estimate.MedianLevel);

            // ceil(0.9 * 4) - 1 = 3 -> the top element.
            Assert.AreEqual(40, estimate.P90Level);
        }

        [TestMethod]
        public void EstimateFromLevels_TenElementsTakesTheNinth()
        {
            var levels = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            var estimate = WorldEventRosterSelector.EstimateFromLevels(levels);

            Assert.AreEqual(10, estimate.Count);
            Assert.AreEqual(5, estimate.MedianLevel);

            // ceil(0.9 * 10) - 1 = 8 -> the ninth element.
            Assert.AreEqual(9, estimate.P90Level);
        }

        // ---- (i) catalog builder ----------------------------------------------------------------------

        [TestMethod]
        public void CatalogBuilder_KeepsAFlaggedCreatureAndSortsByLevelThenWcid()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002606, "emberwrought", 140, 2),
                BuildCreatureWeenie(1002605, "emberwrought", 20, 0),
                BuildCreatureWeenie(1002604, "emberwrought", 20, 1)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join(" | ", diagnostics));
            Assert.AreEqual(3, catalog.MemberCount);

            var members = catalog.Families["emberwrought"];

            Assert.AreEqual(1002604u, members[0].Wcid);
            Assert.AreEqual(1002605u, members[1].Wcid);
            Assert.AreEqual(1002606u, members[2].Wcid);
        }

        [TestMethod]
        public void CatalogBuilder_UnflaggedWeeniesAreSkippedSilently()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(500, "emberwrought", 10, 0, worldEventCreature: false)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(0, diagnostics.Count);
        }

        [TestMethod]
        public void CatalogBuilder_MissingFamilyIsDroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(1002604, null, 20, 0) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "WorldEventFamily");
        }

        [TestMethod]
        public void CatalogBuilder_BadFamilyIdIsDroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(1002604, "Ember Wrought", 20, 0) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "not a valid id");
        }

        [TestMethod]
        public void CatalogBuilder_NonCreatureIsDroppedWithADiagnostic()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002604, "emberwrought", 20, 0, type: WeenieType.Generic)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "not Creature");
        }

        [TestMethod]
        public void CatalogBuilder_NonAttackableIsDroppedWithADiagnostic()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002604, "emberwrought", 20, 0, attackable: false)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "Attackable");
        }

        [TestMethod]
        public void CatalogBuilder_MissingLevelIsDroppedWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(1002604, "emberwrought", null, 0) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "Level is missing");
        }

        [TestMethod]
        public void CatalogBuilder_MissingRoleIsKeptAsTrashWithADiagnostic()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(1002604, "emberwrought", 20, null) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(1, catalog.MemberCount);
            Assert.AreEqual(0, catalog.Families["emberwrought"][0].Role);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "treated as 0");
        }

        [TestMethod]
        public void CatalogBuilder_OutOfRangeRoleIsDropped()
        {
            var weenies = new List<Weenie> { BuildCreatureWeenie(1002604, "emberwrought", 20, 7) };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "outside 0..3");
        }

        [TestMethod]
        public void CatalogBuilder_ObjectiveFlaggedWeenieIsDropped()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002603, "emberwrought", 20, 0, objectiveFlag: true)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out var diagnostics);

            Assert.AreEqual(0, catalog.MemberCount);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "WorldEventObjective");
        }

        [TestMethod]
        public void CatalogBuilder_DescribeCoverageReportsBandAndRoles()
        {
            var weenies = new List<Weenie>
            {
                BuildCreatureWeenie(1002604, "emberwrought", 20, 0),
                BuildCreatureWeenie(1002605, "emberwrought", 60, 0),
                BuildCreatureWeenie(1002606, "emberwrought", 100, 1),
                BuildCreatureWeenie(1002607, "emberwrought", 140, 2)
            };

            var catalog = WorldEventCatalogBuilder.Build(weenies, out _);

            Assert.AreEqual("emberwrought: 4 members, levels 20-140, trash 2 elite 1 champion 1 named 0, flagged 4 table 0, casters 0, minLevel 20",
                catalog.DescribeCoverage("emberwrought"));

            Assert.AreEqual("nosuchfamily: no members", catalog.DescribeCoverage("nosuchfamily"));
        }

        // ---- (j) compose log line ---------------------------------------------------------------------

        [TestMethod]
        public void ToLogLine_MatchesTheFixedFormat()
        {
            var composition = BuildComposition();

            Assert.AreEqual(
                "[WORLDEVENT] run=7 compose source=ambush family=emberwrought boss=none goal=kill_count reward=standard anchor=here",
                composition.ToLogLine(7));

            Assert.AreEqual(
                "source=ambush family=emberwrought boss=none goal=kill_count reward=standard anchor=here",
                composition.ToAxisSummary());
        }

        [TestMethod]
        public void ToLogLine_TwoFamilies_JoinTheSameFieldWithAPlus()
        {
            // Two-family composition (2026-08-29): the pair is ONE token in the SAME field position, so the
            // fixed format in TECH-DESIGN 5.2 keeps its field count for the monitoring queries written
            // against it.
            var twoFamilies = new WorldEventComposition(BuildSource(),
                new[] { BuildFamily("drudge"), BuildFamily("virindi") },
                BossDef.None, BuildGoal(), BuildReward(), BuildAnchor(),
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);

            Assert.AreEqual(
                "[WORLDEVENT] run=7 compose source=ambush family=drudge+virindi boss=none goal=kill_count reward=standard anchor=here",
                twoFamilies.ToLogLine(7));

            Assert.AreEqual(
                "source=ambush family=drudge+virindi boss=none goal=kill_count reward=standard anchor=here",
                twoFamilies.ToAxisSummary());

            Assert.AreEqual("drudge+virindi", twoFamilies.Roster.Id);
            Assert.AreEqual("drudge", twoFamilies.Family.Id);
        }

        // ---- (k) composer refusal matrix --------------------------------------------------------------

        private static WorldEventAxisStore BuildStore()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""edges"",
                ""geometryRadius"": 45.0, ""geometryPoints"": 3, ""waveIntervalSeconds"": 45.0,
                ""maxAlive"": 24, ""waveCount"": { ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 60.0,
                ""compatibleGoals"": [ ""kill_count"" ],
                ""startFlavour"": ""x"", ""waveFlavour"": ""y"" } ] }";

            const string families = @"{ ""families"": [ {
                ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"",
                ""hueKey"": ""ember"", ""biomeTags"": [ ""volcanic"" ] } ] }";

            const string goals = @"{ ""goals"": [
                { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
                  ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                  ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" },
                { ""id"": ""destroy_source"", ""displayName"": ""Destroy Source"", ""type"": ""DestroySource"",
                  ""holdSeconds"": 0, ""mvpRule"": ""killingBlow"", ""progressTemplate"": ""x"" } ] }";

            const string rewards = @"{ ""rewards"": [ {
                ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
                ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
                ""participantsPerCache"": 8, ""claimWindowSeconds"": 300,
                ""gateByCharacter"": true, ""gateByAccount"": true, ""gateByIp"": true } ] }";

            return WorldEventAxisStore.Parse(sources, families, goals, rewards, null);
        }

        private static WorldEventCatalog BuildCatalog(string familyId = "emberwrought")
        {
            return WorldEventCatalogBuilder.Build(
                new List<Weenie> { BuildCreatureWeenie(1002604, familyId, 20, 0) }, out _);
        }

        private static WorldEventRequest BuildRequest()
        {
            return new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnchorPosition = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test"
            };
        }

        [TestMethod]
        public void Compose_HappyPath_DefaultsBossAndReward()
        {
            var request = BuildRequest();

            Assert.IsTrue(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), request, out var composition, out var error), error);

            Assert.AreEqual("none", composition.Boss.Id);
            Assert.AreEqual("standard", composition.Reward.Id);
            Assert.AreEqual("here", composition.Anchor.Id);
            Assert.AreEqual("the test anchor", composition.Anchor.DisplayName);
            Assert.AreEqual(1, composition.Family.Members.Count);
            Assert.AreEqual(1002604u, composition.Family.Members[0].Wcid);

            // The family def on the composition must be a COPY: filling its roster must not reach the store.
            var store = composition.StoreSnapshot;
            Assert.AreEqual(0, store.Families["emberwrought"].Members.Count);
        }

        [TestMethod]
        public void Compose_MissingRequiredIds()
        {
            var store = BuildStore();
            var catalog = BuildCatalog();

            var noSource = BuildRequest();
            noSource.SourceId = null;
            Assert.IsFalse(WorldEventComposer.TryCompose(store, catalog, noSource, out _, out var e1));
            Assert.AreEqual("missing --source", e1);

            var noFamily = BuildRequest();
            noFamily.FamilyId = "";
            Assert.IsFalse(WorldEventComposer.TryCompose(store, catalog, noFamily, out _, out var e2));
            Assert.AreEqual("missing --family", e2);

            var noGoal = BuildRequest();
            noGoal.GoalId = null;
            Assert.IsFalse(WorldEventComposer.TryCompose(store, catalog, noGoal, out _, out var e3));
            Assert.AreEqual("missing --goal", e3);
        }

        [TestMethod]
        public void Compose_UnknownIdNamesTheKnownOnes()
        {
            var request = BuildRequest();
            request.SourceId = "siege";

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), request, out _, out var error));
            Assert.AreEqual("unknown source 'siege' (known: ambush)", error);
        }

        [TestMethod]
        public void Compose_IncompatibleGoalIsRefused()
        {
            var request = BuildRequest();
            request.GoalId = "destroy_source";

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), request, out _, out var error));
            Assert.AreEqual("goal 'destroy_source' is not compatible with source 'ambush' (compatible: kill_count)", error);
        }

        [TestMethod]
        public void Compose_FamilyWithNoCatalogMembersIsRefused()
        {
            var request = BuildRequest();

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), WorldEventCatalog.Empty, request, out _, out var error));
            StringAssert.Contains(error, "has no catalog members");
        }

        [TestMethod]
        public void Compose_FamilyInNeitherStoreNorCatalogIsUnknown()
        {
            var request = BuildRequest();
            request.FamilyId = "nosuchfamily";

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), request, out _, out var error));
            Assert.AreEqual("unknown family 'nosuchfamily' (known: emberwrought)", error);
        }

        [TestMethod]
        public void Compose_CatalogOnlyFamilyIsSynthesized()
        {
            var request = BuildRequest();
            request.FamilyId = "throwaway";

            var catalog = BuildCatalog("throwaway");

            Assert.IsTrue(WorldEventComposer.TryCompose(BuildStore(), catalog, request, out var composition, out var error), error);

            Assert.AreEqual("throwaway", composition.Family.Id);
            Assert.AreEqual("throwaway", composition.Family.DisplayName);
            Assert.AreEqual(0, composition.Family.BiomeTags.Count);
            Assert.AreEqual(1, composition.Family.Members.Count);
        }

        [TestMethod]
        public void Compose_UnknownAnchorIsRefusedAndAMissingAnchorIsNamed()
        {
            var byId = BuildRequest();
            byId.AnchorId = "holtburg_gate";

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), byId, out _, out var e1));
            Assert.AreEqual("unknown anchor 'holtburg_gate' (known: none)", e1);

            var none = BuildRequest();
            none.AnchorPosition = null;

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), none, out _, out var e2));
            StringAssert.Contains(e2, "missing anchor");
        }

        [TestMethod]
        public void Compose_ExplicitRewardIdMustExist()
        {
            var request = BuildRequest();
            request.RewardId = "deluxe";

            Assert.IsFalse(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), request, out _, out var error));
            Assert.AreEqual("unknown reward 'deluxe' (known: standard)", error);
        }

        [TestMethod]
        public void Compose_IdsAreCaseInsensitive()
        {
            var request = BuildRequest();
            request.SourceId = "  AMBUSH ";
            request.FamilyId = "EmberWrought";
            request.GoalId = "Kill_Count";

            Assert.IsTrue(WorldEventComposer.TryCompose(BuildStore(), BuildCatalog(), request, out var composition, out var error), error);
            Assert.AreEqual("ambush", composition.Source.Id);
        }

        [TestMethod]
        public void Compose_NullStoreIsRefused()
        {
            Assert.IsFalse(WorldEventComposer.TryCompose(null, BuildCatalog(), BuildRequest(), out _, out var error));
            Assert.AreEqual("no axes loaded", error);
        }

        // ---- (i) WP-03: the landblock hold and the spawn paths are inert without a world ----------------

        /// <summary>
        /// The floor through its real seam: the sampler hands SetAudience a genuine zero (a staff-only
        /// field, or a sampler that threw), and Audience reads back as one level-50 participant. SetAudience
        /// is private, so this goes through Stage, which is the production path that calls it.
        /// </summary>
        [TestMethod]
        public void Stage_AnEmptyAudienceSampleReadsBackAsTheScalingFloor()
        {
            var composition = new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, BossDef.None, BuildGoal(),
                BuildReward(), BuildAnchor(), null, WorldEventAxisStore.Empty);

            var evt = new WorldEvent(101, composition, new WorldEventRequest { AnnounceLeadSeconds = 0, Invoker = "test" },
                new FakeObjective(), () => new AudienceEstimate(0, 0, 0), () => T0);

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);

            Assert.AreEqual(WorldEventRosterSelector.FloorParticipantCount, evt.Audience.Count,
                "a zero sample is floored at the single write point");
            Assert.AreEqual(WorldEventRosterSelector.FloorParticipantLevel, evt.Audience.MedianLevel);
            Assert.AreEqual(WorldEventRosterSelector.FloorParticipantLevel, evt.Audience.P90Level);
            Assert.AreEqual(4d / 121d, evt.Audience.PowerSum, 0.0000001, "(50/275)^2, not 0");
        }

        [TestMethod]
        public void Stage_ARealAudienceSampleIsNotRewrittenByTheFloor()
        {
            var composition = new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, BossDef.None, BuildGoal(),
                BuildReward(), BuildAnchor(), null, WorldEventAxisStore.Empty);

            var evt = new WorldEvent(102, composition, new WorldEventRequest { AnnounceLeadSeconds = 0, Invoker = "test" },
                new FakeObjective(), () => new AudienceEstimate(4, 180, 220), () => T0);

            evt.Stage(T0);

            Assert.AreEqual(4, evt.Audience.Count);
            Assert.AreEqual(180, evt.Audience.MedianLevel);
            Assert.AreEqual(220, evt.Audience.P90Level);
        }

        [TestMethod]
        public void Stage_WithANullAnchorPosition_DoesNotThrowAndStillAnnounces()
        {
            // A directly constructed WorldEvent has no landblock bridge, and this one additionally has no
            // anchor position at all. The hold, the geometry and every spawn path must skip rather than
            // reach into LandblockManager (TECH-DESIGN D6 - no test may stand up a world).
            var composition = new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, BossDef.None, BuildGoal(),
                BuildReward(), BuildAnchor(), null, WorldEventAxisStore.Empty);

            var evt = new WorldEvent(99, composition, new WorldEventRequest { AnnounceLeadSeconds = 0, Invoker = "test" },
                new FakeObjective(), () => WorldEventRosterSelector.FloorEstimate, () => T0);

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);
            Assert.IsNull(evt.AnchorLandblock);

            evt.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, evt.State);

            // A full wave interval later: the wave tick must still be a no-op without a held landblock.
            evt.Tick(T0 + 60);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(0, evt.Spawner.LiveCount);
            Assert.AreEqual(0, evt.Spawner.LiveTotal);
            Assert.AreEqual(0u, evt.Spawner.BossGuid);
            Assert.AreEqual(0, evt.Spawner.SourceGuids.Count);

            evt.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(WorldEventState.Done, evt.State);
        }

        [TestMethod]
        public void Stage_WithAnAnchorPositionButNoBridge_SkipsTheHoldAndEverySpawn()
        {
            var evt = BuildEvent(new FakeObjective(), announceLeadSeconds: 0);

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.IsNull(evt.AnchorLandblock, "no bridge means no hold");
            Assert.IsFalse(evt.IsHeldLandblock(0), "nothing is held, so no landblock key is ever accepted");
            Assert.AreEqual(0, evt.Spawner.LiveCount);
        }

        [TestMethod]
        public void HeldSnapshot_IsACopy_AndAddHeldTracksBothCollections()
        {
            var evt = BuildEvent(new FakeObjective());

            var before = evt.HeldSnapshot();

            evt.HeldObjects.Add(null);

            Assert.AreEqual(0, before.Count, "the snapshot must not alias the live list");
            Assert.AreEqual(1, evt.HeldSnapshot().Count);

            Assert.IsFalse(evt.AddHeld(null), "a null add is refused");
            Assert.AreEqual(1, evt.HeldSnapshot().Count);
            Assert.AreEqual(0, evt.HeldGuids.Count);
        }

        [TestMethod]
        public void Spawner_StartsWithNothingAliveInEitherCount()
        {
            var evt = BuildEvent(new FakeObjective());

            // LiveCount is wave pressure (what MaxAlive budgets); LiveTotal adds the objective spawns and
            // the champion. Populating either needs a live Creature, which D6 forbids a test from building,
            // so the split itself is verified in PickWave's currentAlive arithmetic instead.
            Assert.AreEqual(0, evt.Spawner.LiveCount);
            Assert.AreEqual(0, evt.Spawner.LiveTotal);
            Assert.IsFalse(evt.Spawner.IsClosed);
        }

        // ---- (j) WP-03: creatures die at Finish, caches survive to the end of the claim window -----------

        private static List<string> CleanupLines(log4net.Appender.MemoryAppender appender, uint runId)
        {
            return appender.GetEvents()
                .Select(e => e.RenderedMessage)
                .Where(m => m != null && m.Contains($"run={runId} cleanup "))
                .ToList();
        }

        /// <summary>
        /// TECH-DESIGN 2.2 step 5 says everything a run spawned is destroyed at Finish; 2.7 says the Weave
        /// Caches live until the claim window closes. The resolution is a two-pass cleanup: the creature
        /// pass runs at Finish (cache-exempt), the final sweep runs at window end.
        ///
        /// A null entry in the held list is the cheapest way to make the list non-empty without building a
        /// WorldObject, which D6 forbids - so what this test pins is the PASS STRUCTURE (when each pass
        /// runs, and that the fixed 5.2 line is emitted exactly once). The cache/creature predicate itself
        /// is pinned separately by <see cref="IsCache_IsTheWeaveCacheMarkerProperty"/>.
        /// </summary>
        [TestMethod]
        public void Finish_Success_RunsTheCreatureCleanupImmediately_NotAtTheEndOfTheClaimWindow()
        {
            var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)
                log4net.LogManager.GetRepository(typeof(WorldEvent).Assembly);

            var appender = new log4net.Appender.MemoryAppender();
            appender.ActivateOptions();

            var priorLevel = hierarchy.Root.Level;
            var priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = log4net.Core.Level.All;
            hierarchy.Configured = true;

            try
            {
                var evt = BuildEvent(new FakeObjective());

                evt.Stage(T0);
                evt.Tick(T0);

                evt.HeldObjects.Add(null);

                evt.Finish(WorldEventOutcome.Success);

                Assert.AreEqual(WorldEventState.Rewarding, evt.State);

                var atFinish = CleanupLines(appender, evt.RunId);

                Assert.AreEqual(1, atFinish.Count,
                    "the creature cleanup must run at Finish, not at window end: " + string.Join(" | ", atFinish));
                StringAssert.Contains(atFinish[0], "survivors=none");
                StringAssert.Contains(atFinish[0], "destroyed=0");

                Assert.IsTrue(evt.Spawner.IsClosed, "no creature may be adopted after the creature pass");
                Assert.IsFalse(evt.HeldClosed, "the held list stays open for the caches during the claim window");

                evt.Tick(T0 + TestClaimWindowSeconds);

                Assert.AreEqual(WorldEventState.Done, evt.State);
                Assert.IsTrue(evt.HeldClosed, "the final sweep closes the held list");

                var atWindowEnd = CleanupLines(appender, evt.RunId);

                Assert.AreEqual(1, atWindowEnd.Count,
                    "5.2 fixes one cleanup line per run; the final sweep must not emit a second: "
                    + string.Join(" | ", atWindowEnd));
            }
            finally
            {
                hierarchy.Root.RemoveAppender(appender);
                hierarchy.Root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
                appender.Close();
            }
        }

        [TestMethod]
        public void Finish_Aborted_ClosesTheHeldListInItsSingleCleanupPass()
        {
            // An aborted run never reaches Rewarding, so no cache exists and the final sweep is the only
            // pass - which is why that pass, and not the creature pass, is what closes the held list.
            var evt = BuildEvent(new FakeObjective());

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.IsFalse(evt.HeldClosed);

            evt.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.IsTrue(evt.HeldClosed);
            Assert.IsFalse(evt.AddHeld(null), "nothing may be adopted once the held list is closed");
        }

        [TestMethod]
        public void IsCache_IsTheWeaveCacheMarkerProperty()
        {
            // The predicate that splits the two cleanup passes. A null entry is NOT a cache, which is why
            // it stays in the creature pass's target list and is skipped by the destroy loop rather than
            // being carried to the final sweep.
            Assert.IsFalse(WorldEventSpawner.IsCache(null));
        }

        // ---- (l) WP-16: per-wave anchor resampling for the disc geometry --------------------------------

        /// <summary>
        /// The pure rule behind WorldEvent.WaveAnchors (D6 - no landblock, no live event needed): a disc
        /// theme resamples fresh anchors on every call, so consecutive waves land at different points under
        /// the same stack; every other geometry hands back the staged (Stage-time) anchors unchanged, exactly
        /// as before WP-16.
        /// </summary>
        /// <summary>
        /// clearanceRadius only decides which town spots may host a source; spawns still scatter over the full
        /// geometryRadius. A disc of geometry 25 / clearance 0 must still place waves far from its centre.
        /// </summary>
        [TestMethod]
        public void ResolveWaveAnchors_DiscUsesGeometryRadius_NotClearanceRadius()
        {
            var anchor = new Position(0x016C019E, 96f, 96f, 0f, 0f, 0f, 0f, 1f, 0);
            var theme = BuildSource();
            theme.GeometryKind = SourceGeometry.Disc;
            theme.GeometryRadius = 25f;
            theme.ClearanceRadiusOverride = 0f;
            theme.GeometryPoints = 4;

            var farthest = 0.0;
            var rng = new Random(7);

            for (var i = 0; i < 50; i++)
                foreach (var p in WorldEvent.ResolveWaveAnchors(theme, anchor, new List<Position>(), rng))
                    farthest = Math.Max(farthest, Math.Sqrt(Math.Pow(p.PositionX - 96f, 2) + Math.Pow(p.PositionY - 96f, 2)));

            Assert.IsTrue(farthest > 10.0, $"200 samples over a 25 m disc stayed within {farthest:0.##} m of the centre");
            Assert.IsTrue(farthest <= 25.0 + 1e-3, $"a sample landed {farthest:0.##} m out, beyond geometryRadius");
        }

        [TestMethod]
        public void ResolveWaveAnchors_DiscIsResampledEveryCall_RingKeepsTheStagedAnchors()
        {
            var anchor = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0);
            var staged = WorldEventGeometry.Ring(anchor, 25f, 4, 0f, new Random(1));

            var ringTheme = BuildSource();
            ringTheme.GeometryKind = SourceGeometry.Ring;

            var ringA = WorldEvent.ResolveWaveAnchors(ringTheme, anchor, staged, new Random(1));
            var ringB = WorldEvent.ResolveWaveAnchors(ringTheme, anchor, staged, new Random(2));

            Assert.AreSame(staged, ringA, "a ring theme must hand back the staged anchors unchanged");
            Assert.AreSame(staged, ringB);

            var discTheme = BuildSource();
            discTheme.GeometryKind = SourceGeometry.Disc;
            discTheme.GeometryRadius = 25f;
            discTheme.GeometryPoints = 4;

            var discA = WorldEvent.ResolveWaveAnchors(discTheme, anchor, staged, new Random(1));
            var discB = WorldEvent.ResolveWaveAnchors(discTheme, anchor, staged, new Random(2));

            Assert.AreNotSame(staged, discA, "a disc theme must resample rather than reuse the staged anchors");
            Assert.AreEqual(discTheme.GeometryPoints, discA.Count);

            var identical = discA.Zip(discB, (x, y) => Math.Abs(x.PositionX - y.PositionX) < 1e-6
                                            && Math.Abs(x.PositionY - y.PositionY) < 1e-6).All(same => same);

            Assert.IsFalse(identical, "two different rng draws must produce different disc waves");
        }
    }
}
