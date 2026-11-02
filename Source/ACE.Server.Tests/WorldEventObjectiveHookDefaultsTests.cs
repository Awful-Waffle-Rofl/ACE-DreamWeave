using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events refactor R4: the defaulted objective hooks on <see cref="IWorldEventObjective"/>.
    ///
    /// Two halves. An objective that overrides nothing must leave the run exactly as it was before the hooks
    /// existed - asserted against hard-coded values, never against a second run of the same code. And an
    /// objective that overrides a hook must actually move the run, which proves WorldEvent consults the hook
    /// at its seam rather than merely declaring it.
    ///
    /// No database and no Player. Most tests run with no landblock bridge, so every spawn path is skipped;
    /// the hold tests use <see cref="FakeBridge"/> and never go past the parts of the run that read only the
    /// landblock's id and adjacents.
    /// </summary>
    [TestClass]
    public class WorldEventObjectiveHookDefaultsTests
    {
        private static readonly double T0 = 1_700_000_000d;

        private const int TestClaimWindowSeconds = 120;

        private const float ThemeRewardRadius = 60f;

        // ---- fakes -----------------------------------------------------------------------------------

        /// <summary>Implements only the required members: every hook takes the interface default.</summary>
        private class PlainObjective : IWorldEventObjective
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

        /// <summary>Overrides every hook, each switchable, and records every call.</summary>
        private sealed class HookedObjective : PlainObjective, IWorldEventObjective
        {
            public bool HoldsAdjacent;
            public float? Radius;
            public bool ReplacesCadence;
            /// <summary>FirstWaveReady answers false this many times, then true.</summary>
            public int NotReadyAnswers;
            public readonly List<double> FirstWaveReadyCalls = new List<double>();
            public WorldEventOutcome? Failure;
            public bool Freezes;
            public WorldEventClaimPolicy Policy = WorldEventClaimPolicy.Default;
            public bool ThrowFromHooks;
            public bool ThrowFromProgressText;
            public double AfkSeconds;
            public float? ParticipantRadius;
            public bool Local;

            string IWorldEventObjective.ProgressText => ThrowFromProgressText ? throw new InvalidOperationException("progress threw") : ProgressText;

            public readonly List<double> StagedCalls = new List<double>();
            public readonly List<(WorldEventOutcome Outcome, double Now)> FinishCalls = new List<(WorldEventOutcome, double)>();

            private T Read<T>(T value) => ThrowFromHooks ? throw new InvalidOperationException("hook threw") : value;

            bool IWorldEventObjective.HoldsAdjacentLandblocks => Read(HoldsAdjacent);
            float? IWorldEventObjective.ArenaRadius => Read(Radius);
            bool IWorldEventObjective.ReplacesWaveCadence => Read(ReplacesCadence);
            bool IWorldEventObjective.FirstWaveReady(double now)
            {
                FirstWaveReadyCalls.Add(now);

                if (NotReadyAnswers > 0)
                {
                    NotReadyAnswers--;
                    return Read(false);
                }

                return Read(true);
            }
            WorldEventOutcome? IWorldEventObjective.FailureOutcome => Read(Failure);
            bool IWorldEventObjective.FreezesProgressAtFinish => Read(Freezes);
            WorldEventClaimPolicy IWorldEventObjective.ClaimPolicy => Read(Policy);
            double IWorldEventObjective.PresenceAfkSeconds => Read(AfkSeconds);
            float? IWorldEventObjective.ParticipantRadius => Read(ParticipantRadius);
            bool IWorldEventObjective.AnnouncesLocally => Read(Local);
            public bool Staff;
            public string OutcomeOverride;
            bool IWorldEventObjective.CountsStaffTowardAudience => Read(Staff);
            string IWorldEventObjective.OutcomeTemplate(string template, bool success) => Read(OutcomeOverride ?? template);

            void IWorldEventObjective.OnStaged(double now)
            {
                StagedCalls.Add(now);
                Read(0);
            }

            void IWorldEventObjective.OnFinish(WorldEventOutcome outcome, double now)
            {
                FinishCalls.Add((outcome, now));
                Read(0);
            }
        }

        /// <summary>
        /// The landblock bridge, faked. Hold returns a Landblock built WITHOUT its constructor (which reads the
        /// client dats), with an empty Adjacents list, so the hold reads Id 0 / Instance 0 and holds one key.
        /// Nothing the tests below drive touches the landblock beyond those reads.
        /// </summary>
        private sealed class FakeBridge : IWorldEventLandblockBridge
        {
            public bool ReturnNull;
            public readonly List<bool> HoldCalls = new List<bool>();

            public IReadOnlyDictionary<ulong, bool> SnapshotPermaload() => new Dictionary<ulong, bool>();

            public Landblock Hold(Position anchor, bool loadAdjacents)
            {
                HoldCalls.Add(loadAdjacents);

                if (ReturnNull)
                    return null;

                var landblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
                landblock.Adjacents = new List<Landblock>();
                return landblock;
            }

            public void ReleasePermaload(IReadOnlyList<ulong> keys)
            {
            }
        }

        /// <summary>Runs <paramref name="body"/> with every log line captured, and returns the rendered messages.</summary>
        private static List<string> CaptureLog(Action body)
        {
            var appender = new MemoryAppender();
            var hierarchy = (Hierarchy)LogManager.GetRepository(typeof(WorldEvent).Assembly);
            var priorLevel = hierarchy.Root.Level;
            var priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.All;
            hierarchy.Configured = true;

            try
            {
                body();
            }
            finally
            {
                hierarchy.Root.RemoveAppender(appender);
                hierarchy.Root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
            }

            return appender.GetEvents().Select(e => e.RenderedMessage).ToList();
        }

        /// <summary>The pre-R4 hold line for this fixture (run 1, one key, nothing permaloaded before), verbatim.</summary>
        private const string PreR4HoldLine = "[WORLDEVENT] run=1 hold blocks=1 release-at-finish=1 anchor=0x00000000 instance=0";

        // ---- builders (the WorldEventStateMachineTests shapes) ----------------------------------------

        private static SourceThemeDef BuildSource(bool holdAdjacents = false)
        {
            return new SourceThemeDef
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
                HoldAdjacentLandblocks = holdAdjacents,
                RewardRadius = ThemeRewardRadius,
                CompatibleGoals = new List<string> { "kill_count" },
                StartFlavour = "Something is coming through the weave near {anchor}.",
                WaveFlavour = "Another rank tears its way in!"
            };
        }

        private static GoalDef BuildGoal()
        {
            return new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = MvpRule.MostKills,
                Count = new ScaledCount { Base = 20, PerParticipant = 6, Cap = 150 },
                ProgressTemplate = "{killed} of {target} slain."
            };
        }

        private static RewardDef BuildReward()
        {
            return new RewardDef
            {
                Id = "standard",
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

        private static FamilyDef BuildFamily()
        {
            return new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string> { "volcanic" },
                Members = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 }
                }
            };
        }

        private static WorldEvent BuildEvent(IWorldEventObjective objective, bool themeHoldsAdjacents = false,
            IWorldEventLandblockBridge bridge = null)
        {
            var composition = new WorldEventComposition(BuildSource(themeHoldsAdjacents), new[] { BuildFamily() }, BossDef.None,
                BuildGoal(), BuildReward(),
                new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() },
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                MaxDurationSeconds = WorldEvent.DefaultMaxDurationSeconds,
                MinDurationSeconds = 0,
                AbandonAfterSeconds = WorldEvent.DefaultAbandonAfterSeconds,
                WipeGraceSeconds = WorldEvent.DefaultWipeGraceSeconds,
                Invoker = "test"
            };

            return new WorldEvent(1, composition, request, objective, () => new AudienceEstimate(3, 100, 150), () => T0,
                landblockBridge: bridge);
        }

        /// <summary>Stage, then the first tick (Announced -> Active, lead 0).</summary>
        private static WorldEvent BuildActive(IWorldEventObjective objective, bool themeHoldsAdjacents = false)
        {
            var evt = BuildEvent(objective, themeHoldsAdjacents);

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, evt.State, "fixture: the run must be Active");

            return evt;
        }

        // ---- the H6 shared-core seams: a default objective leaves the run exactly as it was ------------------------

        [TestMethod]
        public void EffectiveParticipantRadius_DefaultIsTheRewardRadius_TheObjectiveCanSetItsOwn()
        {
            Assert.AreEqual(ThemeRewardRadius, BuildEvent(new PlainObjective()).EffectiveParticipantRadius);
            Assert.AreEqual(ThemeRewardRadius, BuildEvent(null).EffectiveParticipantRadius);
            Assert.AreEqual(45f, BuildEvent(new HookedObjective { ParticipantRadius = 45f }).EffectiveParticipantRadius);
            Assert.AreEqual(ThemeRewardRadius, BuildEvent(new HookedObjective { ParticipantRadius = 45f, ThrowFromHooks = true }).EffectiveParticipantRadius,
                "a throwing hook reads as the source's radius");
        }

        [TestMethod]
        public void DeathProtectionRadius_IsTheParticipantRadius_RewardRadiusByDefault()
        {
            Assert.AreEqual(ThemeRewardRadius, WorldEventManager.DeathProtectionRadius(BuildEvent(new PlainObjective())));
            Assert.AreEqual(45f, WorldEventManager.DeathProtectionRadius(BuildEvent(new HookedObjective { ParticipantRadius = 45f })));
            Assert.AreEqual(0f, WorldEventManager.DeathProtectionRadius(null), "no run, no protection radius");
        }

        [TestMethod]
        public void AnnounceInRun_DefaultBroadcasts_LocalObjectiveDoesNot()
        {
            var saved = WorldEventAnnouncer.BroadcastSink;
            var sent = new List<string>();

            try
            {
                WorldEventAnnouncer.BroadcastSink = sent.Add;

                BuildEvent(new PlainObjective()).AnnounceInRun("plain line");
                CollectionAssert.AreEqual(new[] { "plain line" }, sent, "a default objective's in-run line is world-wide, as before");

                BuildEvent(new HookedObjective { Local = true }).AnnounceInRun("local line");
                CollectionAssert.AreEqual(new[] { "plain line" }, sent, "an objective that announces locally never broadcasts");

                BuildEvent(new HookedObjective { Local = true, ThrowFromHooks = true }).AnnounceInRun("throwing hook");
                CollectionAssert.AreEqual(new[] { "plain line", "throwing hook" }, sent, "a throwing hook reads as world-wide");
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = saved;
            }
        }

        [TestMethod]
        public void ObjectiveCountsStaff_DefaultFalse_HookReadThrowingIsFalse()
        {
            Assert.IsFalse(BuildEvent(new PlainObjective()).ObjectiveCountsStaff());
            Assert.IsFalse(BuildEvent(null).ObjectiveCountsStaff());
            Assert.IsTrue(BuildEvent(new HookedObjective { Staff = true }).ObjectiveCountsStaff());
            Assert.IsFalse(BuildEvent(new HookedObjective { Staff = true, ThrowFromHooks = true }).ObjectiveCountsStaff());
        }

        [TestMethod]
        public void CreatureDeath_NonFriendlyIsCredit_APlacedFriendlyIsNot()
        {
            var evt = BuildActive(new PlainObjective());

            evt.LastCreditAt = 0;
            evt.OnCreatureDied(HoldFixtures.MakeCreature("Monster"), null, null);
            Assert.AreEqual(T0, evt.LastCreditAt, "an ordinary event creature's death is credit, as before");

            var friendly = HoldFixtures.MakeCreature("Knight");
            evt.Spawner.AdoptHoldKnightForTest(friendly);

            evt.LastCreditAt = 0;
            evt.OnCreatureDied(friendly, null, null);
            Assert.AreEqual(0d, evt.LastCreditAt, "a friendly the run placed is no credit");
        }

        [TestMethod]
        public void OutcomeTemplate_DefaultIsTheCompositionTemplate_TheObjectiveCanFillIt()
        {
            var saved = WorldEventAnnouncer.BroadcastSink;
            var sent = new List<string>();

            try
            {
                WorldEventAnnouncer.BroadcastSink = sent.Add;

                var plain = new PlainObjective();
                var evt = BuildActive(plain);
                evt.Composition.Goal.SuccessTemplate = "Done at {anchor}.";
                plain.IsComplete = true;
                evt.Tick(T0 + 1);

                Assert.IsTrue(sent.Any(l => l.Contains("Done at the test anchor.")), "the template as written: " + string.Join(" | ", sent));

                sent.Clear();

                var hooked = new HookedObjective { OutcomeOverride = "Filled by the objective." };
                var evt2 = BuildActive(hooked);
                evt2.Composition.Goal.SuccessTemplate = "Done at {anchor}.";
                hooked.IsComplete = true;
                evt2.Tick(T0 + 1);

                Assert.IsTrue(sent.Any(l => l.Contains("Filled by the objective.")), "the objective's template: " + string.Join(" | ", sent));
                Assert.IsFalse(sent.Any(l => l.Contains("Done at")));
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = saved;
            }
        }

        [TestMethod]
        public void SpawnCaches_ParticipantsPerCache_DefaultIsTheAxis_PolicyCanSetIt()
        {
            var reward = BuildReward();

            Assert.AreEqual(8, WorldEventRewardDelivery.ParticipantsPerCacheFor(BuildEvent(new PlainObjective()), reward));
            Assert.AreEqual(8, WorldEventRewardDelivery.ParticipantsPerCacheFor(null, reward));
            Assert.AreEqual(3, WorldEventRewardDelivery.ParticipantsPerCacheFor(
                BuildEvent(new HookedObjective { Policy = WorldEventClaimPolicy.Default with { ParticipantsPerCache = 3 } }), reward));
            Assert.AreEqual(8, WorldEventRewardDelivery.ParticipantsPerCacheFor(
                BuildEvent(new HookedObjective { Policy = WorldEventClaimPolicy.Default with { ParticipantsPerCache = 0 } }), reward), "0 never wins");
        }

        // ---- the interface defaults themselves --------------------------------------------------------

        [TestMethod]
        public void InterfaceDefaults_AreTheBaselineValues()
        {
            IWorldEventObjective o = new PlainObjective();

            Assert.IsFalse(o.HoldsAdjacentLandblocks);
            Assert.IsNull(o.ArenaRadius);
            Assert.IsFalse(o.ReplacesWaveCadence);
            Assert.IsTrue(o.FirstWaveReady(T0));
            Assert.IsNull(o.FailureOutcome);
            Assert.IsFalse(o.FreezesProgressAtFinish);
            Assert.AreSame(WorldEventClaimPolicy.Default, o.ClaimPolicy);
            Assert.AreEqual(0.0, o.PresenceAfkSeconds);
            Assert.IsNull(o.ParticipantRadius);
            Assert.IsFalse(o.AnnouncesLocally);
            Assert.IsFalse(o.CountsStaffTowardAudience);
            Assert.AreEqual("{goal} won", o.OutcomeTemplate("{goal} won", true));
            Assert.IsNull(o.OutcomeTemplate(null, false));

            // The two void hooks are no-ops: calling them must not throw.
            o.OnStaged(T0);
            o.OnFinish(WorldEventOutcome.Success, T0);
        }

        /// <summary>
        /// Every production objective takes every hook's default: the invariant that this PR changes nothing for
        /// the goals that ship. A new IWorldEventObjective implementation fails the enumeration on purpose, so it
        /// gets looked at.
        /// </summary>
        [TestMethod]
        public void ProductionObjectives_AreExactlyTheKnownThree_AndOverrideNoHook()
        {
            var assembly = typeof(IWorldEventObjective).Assembly;

            var implementations = assembly.GetTypes()
                .Where(t => typeof(IWorldEventObjective).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            // HoldObjective (World Events Hold H0) is the fourth: the shipping goals' three still override nothing, which is
            // what this test pins. Hold is gated off (world_events_hold_enabled) and listed by no source.
            CollectionAssert.AreEqual(new List<string> { "DestroySourceObjective", "HoldObjective", "KillBossObjective", "KillCountObjective" }, implementations);

            var hooks = new[]
            {
                "get_HoldsAdjacentLandblocks", "get_ArenaRadius", "get_ReplacesWaveCadence", "FirstWaveReady",
                "OnStaged", "get_FailureOutcome", "get_FreezesProgressAtFinish", "OnFinish", "get_ClaimPolicy",
                "get_PresenceAfkSeconds", "get_ParticipantRadius", "get_AnnouncesLocally",
                "get_CountsStaffTowardAudience", "OutcomeTemplate"
            };

            Assert.AreEqual(hooks.Length, typeof(IWorldEventObjective).GetMethods().Count(m => hooks.Contains(m.Name)),
                "every hook named here exists on the interface (a renamed hook must not drop out of this check)");

            foreach (var type in new[] { typeof(KillCountObjective), typeof(DestroySourceObjective), typeof(KillBossObjective) })
            {
                var map = type.GetInterfaceMap(typeof(IWorldEventObjective));

                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (!hooks.Contains(map.InterfaceMethods[i].Name))
                        continue;

                    Assert.AreEqual(typeof(IWorldEventObjective), map.TargetMethods[i].DeclaringType,
                        $"{type.Name} overrides {map.InterfaceMethods[i].Name}; R4 is no-behaviour-change for shipping goals");
                }
            }
        }

        // ---- an objective that overrides nothing: the pre-R4 run, hard-coded ---------------------------

        [TestMethod]
        public void PlainObjective_Stage_ThemeArenaAndNoAdjacentReasons()
        {
            var evt = BuildEvent(new PlainObjective());

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);
            Assert.IsNotNull(evt.Arena);
            Assert.AreEqual(ThemeRewardRadius, evt.Arena.Radius, "no arena block: the theme's rewardRadius");
            Assert.AreEqual(ThemeRewardRadius / 2f, evt.Arena.HalfHeight);
            Assert.AreEqual(WorldEventHoldReason.None, evt.AdjacentHoldReasons);
        }

        [TestMethod]
        public void PlainObjective_ThemeHoldingAdjacents_IsTheSourceReasonAlone()
        {
            var evt = BuildEvent(new PlainObjective(), themeHoldsAdjacents: true);

            evt.Stage(T0);

            Assert.AreEqual(WorldEventHoldReason.Source, evt.AdjacentHoldReasons);
        }

        [TestMethod]
        public void PlainObjective_Ticks_RunTheNormalCadenceAndSendWaveZero()
        {
            var objective = new PlainObjective();
            var evt = BuildActive(objective);

            Assert.AreEqual(T0, evt.FirstWaveReleasedAt, "wave 0 goes out on the Active edge");

            evt.Tick(T0 + 1);
            evt.Tick(T0 + 2);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(2, evt.NormalWaveCadenceTicks, "one normal cadence tick per Active tick");
            Assert.AreEqual(2, objective.TickCount);
        }

        [TestMethod]
        public void PlainObjective_Complete_FinishesSuccess_AndProgressStaysLive()
        {
            var objective = new PlainObjective();
            var evt = BuildActive(objective);

            objective.IsComplete = true;
            evt.Tick(T0 + 1);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
            Assert.AreEqual(T0 + TestClaimWindowSeconds, evt.ClaimWindowEndsAt);

            objective.ProgressText = "changed after finish";
            Assert.AreEqual("changed after finish", evt.ProgressText, "nothing freezes the line by default");
        }

        [TestMethod]
        public void PlainObjective_ClaimPolicy_IsTheDefault()
        {
            Assert.AreSame(WorldEventClaimPolicy.Default, WorldEventCacheHandler.ClaimPolicyFor(BuildEvent(new PlainObjective())));
            Assert.AreSame(WorldEventClaimPolicy.Default, WorldEventCacheHandler.ClaimPolicyFor(BuildEvent(null)));
            Assert.AreSame(WorldEventClaimPolicy.Default, WorldEventCacheHandler.ClaimPolicyFor(null));
        }

        // ---- presence idle window (World Events Hold H0): the idle tracker reads the objective at Stage ---------------

        /// <summary>Every shipping objective, staged in a real run, leaves the presence idle rule off (afkSeconds 0), as before the hook.</summary>
        [TestMethod]
        public void PresenceAfkSeconds_IsZero_ForEveryShippingObjective_BeforeAndAfterStage()
        {
            var goal = BuildGoal();

            var shipping = new IWorldEventObjective[]
            {
                new PlainObjective(),
                new KillCountObjective(goal, () => 3, () => Array.Empty<uint>(), null),
                new DestroySourceObjective(goal, () => Array.Empty<uint>()),
                new KillBossObjective(goal, () => 0)
            };

            foreach (var objective in shipping)
            {
                Assert.AreEqual(0.0, objective.PresenceAfkSeconds, objective.GetType().Name);

                var evt = BuildEvent(objective);
                Assert.AreEqual(0.0, evt.PresenceAfkSecondsInEffect, $"{objective.GetType().Name}: off before Stage");

                evt.Stage(T0);
                Assert.AreEqual(WorldEventState.Announced, evt.State, objective.GetType().Name);
                Assert.AreEqual(0.0, evt.PresenceAfkSecondsInEffect, $"{objective.GetType().Name}: still off after Stage");
            }

            Assert.AreEqual(0.0, BuildEvent(null).PresenceAfkSecondsInEffect, "no objective: off");
        }

        /// <summary>Positive control: the idle tracker really is built from the hook at Stage (not just declared).</summary>
        [TestMethod]
        public void PresenceAfkSeconds_FromTheObjective_ReachesTheIdleTrackerAtStage()
        {
            var evt = BuildEvent(new HookedObjective { AfkSeconds = 30 });

            Assert.AreEqual(0.0, evt.PresenceAfkSecondsInEffect, "off until Stage reads the hook");

            evt.Stage(T0);

            Assert.AreEqual(30.0, evt.PresenceAfkSecondsInEffect);
        }

        [TestMethod]
        public void PresenceAfkSeconds_NegativeNonFiniteOrThrowing_ReadsAsOff()
        {
            foreach (var bad in new[] { -5.0, double.NaN, double.PositiveInfinity })
            {
                var evt = BuildEvent(new HookedObjective { AfkSeconds = bad });
                evt.Stage(T0);
                Assert.AreEqual(0.0, evt.PresenceAfkSecondsInEffect, bad.ToString());
            }

            var throwing = BuildEvent(new HookedObjective { AfkSeconds = 30, ThrowFromHooks = true });
            throwing.Stage(T0);
            Assert.AreEqual(0.0, throwing.PresenceAfkSecondsInEffect, "a throwing hook reads as its default");
        }

        [TestMethod]
        public void ParticipantRadiusAndAnnouncesLocally_SeamsReadTheHook_AndDefault()
        {
            var plain = BuildEvent(new PlainObjective());
            Assert.IsNull(plain.ObjectiveParticipantRadius);
            Assert.IsFalse(plain.ObjectiveAnnouncesLocally);

            Assert.AreEqual(45f, BuildEvent(new HookedObjective { ParticipantRadius = 45f }).ObjectiveParticipantRadius);
            Assert.IsTrue(BuildEvent(new HookedObjective { Local = true }).ObjectiveAnnouncesLocally);

            Assert.IsNull(BuildEvent(new HookedObjective { ParticipantRadius = 0f }).ObjectiveParticipantRadius, "non-positive: the source's rule");
            Assert.IsNull(BuildEvent(new HookedObjective { ParticipantRadius = float.NaN }).ObjectiveParticipantRadius);
            Assert.IsNull(BuildEvent(new HookedObjective { ParticipantRadius = 45f, ThrowFromHooks = true }).ObjectiveParticipantRadius);
            Assert.IsFalse(BuildEvent(new HookedObjective { Local = true, ThrowFromHooks = true }).ObjectiveAnnouncesLocally);
            Assert.IsNull(BuildEvent(null).ObjectiveParticipantRadius);
        }

        // ---- each overridden hook moves the run --------------------------------------------------------

        [TestMethod]
        public void FailureOutcome_EndsTheRunWithThatOutcome()
        {
            var objective = new HookedObjective();
            var evt = BuildActive(objective);

            evt.Tick(T0 + 1);
            Assert.AreEqual(WorldEventState.Active, evt.State, "null failure: the run goes on");

            objective.Failure = WorldEventOutcome.FailedWipe;
            evt.Tick(T0 + 2);

            Assert.AreEqual(WorldEventOutcome.FailedWipe, evt.Outcome);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "a Failed* outcome still opens the claim window");
        }

        [TestMethod]
        public void FailureOutcome_BeatsCompletionOnTheSameTick()
        {
            var objective = new HookedObjective();
            var evt = BuildActive(objective);

            objective.IsComplete = true;
            objective.Failure = WorldEventOutcome.FailedTimeout;
            evt.Tick(T0 + 1);

            Assert.AreEqual(WorldEventOutcome.FailedTimeout, evt.Outcome);
        }

        [TestMethod]
        public void FailureOutcome_Aborted_SkipsRewarding()
        {
            var objective = new HookedObjective();
            var evt = BuildActive(objective);

            objective.Failure = WorldEventOutcome.AbortedError;
            evt.Tick(T0 + 1);

            Assert.AreEqual(WorldEventOutcome.AbortedError, evt.Outcome);
            Assert.AreEqual(WorldEventState.Done, evt.State);
        }

        [TestMethod]
        public void ReplacesWaveCadence_SuppressesTheNormalWaveTick()
        {
            var objective = new HookedObjective { ReplacesCadence = true };
            var evt = BuildActive(objective);

            evt.Tick(T0 + 1);
            evt.Tick(T0 + 2);

            Assert.AreEqual(0, evt.NormalWaveCadenceTicks);
            Assert.AreEqual(2, objective.TickCount, "the objective itself still ticks");

            objective.ReplacesCadence = false;
            evt.Tick(T0 + 3);

            Assert.AreEqual(1, evt.NormalWaveCadenceTicks, "read every tick, not latched");
        }

        [TestMethod]
        public void FirstWaveReady_DefaultTrue_ReleasesWaveZeroOnTheActiveEdgeAndAsksOnce()
        {
            var objective = new HookedObjective();
            var evt = BuildActive(objective);

            Assert.AreEqual(T0, evt.FirstWaveReleasedAt);
            CollectionAssert.AreEqual(new List<double> { T0 }, objective.FirstWaveReadyCalls, "asked once, on the Active edge");

            evt.Tick(T0 + 1);

            Assert.AreEqual(1, evt.NormalWaveCadenceTicks, "the cadence runs from the first Active tick");
            Assert.AreEqual(1, objective.FirstWaveReadyCalls.Count, "never asked again once wave 0 has gone");
        }

        /// <summary>
        /// Not ready on the Active edge and the next two ticks: wave 0 goes out on the first true tick (T0 + 3),
        /// that tick runs no cadence (like the Active edge), and the cadence carries on from the tick after.
        /// </summary>
        [TestMethod]
        public void FirstWaveReady_FalseForThreeAsks_ReleasesWaveZeroOnTheFirstTrueTick_ThenTheCadenceContinues()
        {
            var objective = new HookedObjective { NotReadyAnswers = 3 };
            var evt = BuildActive(objective);

            Assert.IsNull(evt.FirstWaveReleasedAt, "not ready on the Active edge");

            evt.Tick(T0 + 1);
            evt.Tick(T0 + 2);

            Assert.IsNull(evt.FirstWaveReleasedAt);
            Assert.AreEqual(0, evt.NormalWaveCadenceTicks, "no cadence while wave 0 waits");

            evt.Tick(T0 + 3);

            Assert.AreEqual(T0 + 3, evt.FirstWaveReleasedAt);
            Assert.AreEqual(0, evt.NormalWaveCadenceTicks, "the release tick runs no cadence");
            CollectionAssert.AreEqual(new List<double> { T0, T0 + 1, T0 + 2, T0 + 3 }, objective.FirstWaveReadyCalls);

            evt.Tick(T0 + 4);
            evt.Tick(T0 + 5);

            Assert.AreEqual(2, evt.NormalWaveCadenceTicks, "the cadence continues");
            Assert.AreEqual(4, objective.FirstWaveReadyCalls.Count, "not asked after the release");
            Assert.AreEqual(WorldEventState.Active, evt.State);
        }

        [TestMethod]
        public void FirstWaveReady_IsNotRetriedByTheNormalCadence_WhenTheObjectiveReplacesIt()
        {
            var objective = new HookedObjective { NotReadyAnswers = 1, ReplacesCadence = true };
            var evt = BuildActive(objective);

            evt.Tick(T0 + 1);
            evt.Tick(T0 + 2);

            Assert.IsNull(evt.FirstWaveReleasedAt);
            Assert.AreEqual(1, objective.FirstWaveReadyCalls.Count, "only the Active edge asked");
        }

        [TestMethod]
        public void HoldsAdjacentLandblocks_AddsTheObjectiveReason()
        {
            var alone = BuildEvent(new HookedObjective { HoldsAdjacent = true });
            alone.Stage(T0);
            Assert.AreEqual(WorldEventHoldReason.Objective, alone.AdjacentHoldReasons);

            var withTheme = BuildEvent(new HookedObjective { HoldsAdjacent = true }, themeHoldsAdjacents: true);
            withTheme.Stage(T0);
            Assert.AreEqual(WorldEventHoldReason.Source | WorldEventHoldReason.Objective, withTheme.AdjacentHoldReasons);
        }

        [TestMethod]
        public void ArenaRadius_ReachesBuildArena()
        {
            var evt = BuildEvent(new HookedObjective { Radius = 25f });

            evt.Stage(T0);

            Assert.IsNotNull(evt.Arena);
            Assert.AreEqual(25f, evt.Arena.Radius, "a positive goal radius wins over the theme's rewardRadius");
            Assert.AreEqual(12.5f, evt.Arena.HalfHeight, "no arena block: half the radius");
        }

        [TestMethod]
        public void ArenaRadius_NonPositive_LeavesTheThemeRule()
        {
            var evt = BuildEvent(new HookedObjective { Radius = 0f });

            evt.Stage(T0);

            Assert.AreEqual(ThemeRewardRadius, evt.Arena.Radius);
        }

        [TestMethod]
        public void OnStaged_IsCalledOnceWithTheStageTime()
        {
            var objective = new HookedObjective();
            var evt = BuildEvent(objective);

            evt.Stage(T0);
            evt.Tick(T0);
            evt.Tick(T0 + 1);

            CollectionAssert.AreEqual(new List<double> { T0 }, objective.StagedCalls);
        }

        [TestMethod]
        public void OnFinish_IsCalledOnceWithTheOutcome()
        {
            var objective = new HookedObjective();
            var evt = BuildActive(objective);

            evt.Finish(WorldEventOutcome.Success);
            evt.Finish(WorldEventOutcome.FailedTimeout);

            Assert.AreEqual(1, objective.FinishCalls.Count);
            Assert.AreEqual(WorldEventOutcome.Success, objective.FinishCalls[0].Outcome);
            Assert.AreEqual(T0, objective.FinishCalls[0].Now);
        }

        [TestMethod]
        public void FreezesProgressAtFinish_KeepsTheLineAsItStoodAtFinish()
        {
            var objective = new HookedObjective { Freezes = true, ProgressText = "3 of 3 stand." };
            var evt = BuildActive(objective);

            objective.ProgressText = "2 of 3 stand.";
            Assert.AreEqual("2 of 3 stand.", evt.ProgressText, "live before Finish");

            evt.Finish(WorldEventOutcome.Success);

            objective.ProgressText = "0 of 3 stand.";
            Assert.AreEqual("2 of 3 stand.", evt.ProgressText);
        }

        [TestMethod]
        public void ClaimPolicy_ReachesTheHandlersPipelineInput()
        {
            var policy = new WorldEventClaimPolicy(FailureRewardMode.None, new ClaimThresholds(30, 0.1));
            var evt = BuildEvent(new HookedObjective { Policy = policy });

            Assert.AreSame(policy, WorldEventCacheHandler.ClaimPolicyFor(evt));
        }

        [TestMethod]
        public void ClaimPolicy_NullFromTheObjective_ReadsAsDefault()
        {
            var evt = BuildEvent(new HookedObjective { Policy = null });

            Assert.AreSame(WorldEventClaimPolicy.Default, WorldEventCacheHandler.ClaimPolicyFor(evt));
        }

        /// <summary>Every hook that throws reads as its default; the run is not taken down by it.</summary>
        [TestMethod]
        public void ThrowingHooks_ReadAsTheirDefaults()
        {
            var objective = new HookedObjective { ThrowFromHooks = true };
            var evt = BuildEvent(objective);

            evt.Stage(T0);

            Assert.AreEqual(WorldEventState.Announced, evt.State);
            Assert.AreEqual(ThemeRewardRadius, evt.Arena.Radius);
            Assert.AreEqual(WorldEventHoldReason.None, evt.AdjacentHoldReasons);
            Assert.AreSame(WorldEventClaimPolicy.Default, WorldEventCacheHandler.ClaimPolicyFor(evt));

            evt.Tick(T0);
            Assert.AreEqual(T0, evt.FirstWaveReleasedAt, "a throwing FirstWaveReady reads as ready");

            evt.Tick(T0 + 1);
            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(1, evt.NormalWaveCadenceTicks);

            objective.IsComplete = true;
            objective.ProgressText = "done";
            evt.Tick(T0 + 2);

            Assert.AreEqual(WorldEventOutcome.Success, evt.Outcome);
            Assert.AreEqual(1, objective.FinishCalls.Count);

            objective.ProgressText = "after";
            Assert.AreEqual("after", evt.ProgressText, "a throwing FreezesProgressAtFinish does not freeze");
        }

        // ---- wave timing, pinned through ShouldSpawnNextWave with the recorded lastWaveAt -------------------
        //
        // Without a held landblock SpawnWave never runs, so lastWaveIndexSpawned stays -1 here; these tests pass
        // waveIndexSpawned 0 ("wave 0 is out") and a live field (liveCount 1, so only the interval path can fire)
        // to ask the pure cadence rule when the NEXT wave is due given the lastWaveAt the run recorded.

        private static bool NextWaveDue(WorldEvent evt, double now)
        {
            var interval = evt.Composition.Source.EffectiveWaveIntervalSeconds;

            return WorldEvent.ShouldSpawnNextWave(true, false, 0, 1, now, evt.LastWaveAt, interval, 10, null);
        }

        [TestMethod]
        public void WaveTiming_Default_NextWaveDueOneIntervalAfterTheActiveEdge()
        {
            var evt = BuildActive(new PlainObjective());
            var interval = evt.Composition.Source.EffectiveWaveIntervalSeconds;

            Assert.AreEqual(45.0, (double)interval, "fixture: the theme's interval");
            Assert.AreEqual(T0, evt.LastWaveAt);
            Assert.AreEqual(-1, evt.lastWaveIndexSpawned, "no landblock: SpawnWave never ran");
            Assert.IsFalse(NextWaveDue(evt, T0 + 44));
            Assert.IsTrue(NextWaveDue(evt, T0 + 45));
        }

        [TestMethod]
        public void WaveTiming_DelayedRelease_NextWaveDueOneIntervalAfterTheRelease()
        {
            var objective = new HookedObjective { NotReadyAnswers = 3 };
            var evt = BuildActive(objective);

            Assert.AreEqual(T0, evt.LastWaveAt, "the Active edge stamps it, as always");

            evt.Tick(T0 + 1);
            evt.Tick(T0 + 2);
            Assert.AreEqual(T0, evt.LastWaveAt, "unchanged while wave 0 waits");

            evt.Tick(T0 + 3);

            Assert.AreEqual(T0 + 3, evt.LastWaveAt, "restarted by the release");
            Assert.IsFalse(NextWaveDue(evt, T0 + 3 + 44));
            Assert.IsTrue(NextWaveDue(evt, T0 + 3 + 45));
        }

        // ---- HoldLandblocks through a bridge fake -----------------------------------------------------------

        [TestMethod]
        public void HoldLandblocks_PlainObjective_HoldsTheAnchorAlone_AndLogsThePreR4LineVerbatim()
        {
            var bridge = new FakeBridge();
            var evt = BuildEvent(new PlainObjective(), bridge: bridge);

            var lines = CaptureLog(() => evt.Stage(T0));

            CollectionAssert.AreEqual(new List<bool> { false }, bridge.HoldCalls);
            Assert.IsNotNull(evt.AnchorLandblock);
            CollectionAssert.Contains(lines, PreR4HoldLine);
        }

        [TestMethod]
        public void HoldLandblocks_ThemeReason_HoldsAdjacents_AndTheLineHasNoSuffix()
        {
            var bridge = new FakeBridge();
            var evt = BuildEvent(new PlainObjective(), themeHoldsAdjacents: true, bridge: bridge);

            var lines = CaptureLog(() => evt.Stage(T0));

            CollectionAssert.AreEqual(new List<bool> { true }, bridge.HoldCalls);
            CollectionAssert.Contains(lines, PreR4HoldLine);
        }

        [TestMethod]
        public void HoldLandblocks_ObjectiveReason_HoldsAdjacents_AndTheLineCarriesTheReasons()
        {
            var bridge = new FakeBridge();
            var evt = BuildEvent(new HookedObjective { HoldsAdjacent = true }, themeHoldsAdjacents: true, bridge: bridge);

            var lines = CaptureLog(() => evt.Stage(T0));

            CollectionAssert.AreEqual(new List<bool> { true }, bridge.HoldCalls);
            CollectionAssert.Contains(lines, PreR4HoldLine + " reasons=source+objective");
            CollectionAssert.DoesNotContain(lines, PreR4HoldLine);
        }

        // ---- review fixes ----------------------------------------------------------------------------------

        /// <summary>
        /// A throw in the Active-edge spawn block (objectives, decor, npcs) costs the run wave 0 and never
        /// retries it, exactly as before R4; the cadence still ticks and still refuses every wave.
        /// </summary>
        [TestMethod]
        public void ActiveEdgeSpawnThrow_WaveZeroIsNeverReleased_AndTheCadenceStillTicks()
        {
            var objective = new HookedObjective();
            var evt = BuildEvent(objective, bridge: new FakeBridge());

            evt.activeEdgeSpawnSeam = _ => throw new InvalidOperationException("spawn threw");

            evt.Stage(T0);
            evt.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.IsNull(evt.FirstWaveReleasedAt);

            evt.Tick(T0 + 1);
            evt.Tick(T0 + 2);

            Assert.IsNull(evt.FirstWaveReleasedAt, "not retried a tick later");
            Assert.AreEqual(2, evt.NormalWaveCadenceTicks);
            Assert.AreEqual(0, objective.FirstWaveReadyCalls.Count, "never asked: the throw came first");
            Assert.AreEqual(-1, evt.lastWaveIndexSpawned, "no wave ever went out");
        }

        [TestMethod]
        public void FailureOutcome_WithAThrowingProgressText_StillEndsTheRun()
        {
            var objective = new HookedObjective { ThrowFromProgressText = true };
            var evt = BuildActive(objective);

            objective.Failure = WorldEventOutcome.FailedWipe;
            evt.Tick(T0 + 1);

            Assert.AreEqual(WorldEventOutcome.FailedWipe, evt.Outcome);
            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
        }

        [TestMethod]
        public void FailureOutcome_NonFailureValues_AreIgnored()
        {
            foreach (var notAFailure in new[] { WorldEventOutcome.Success, WorldEventOutcome.SuccessBossAbsent, WorldEventOutcome.None })
            {
                var objective = new HookedObjective { Failure = notAFailure };
                var evt = BuildActive(objective);

                evt.Tick(T0 + 1);

                Assert.AreEqual(WorldEventState.Active, evt.State, $"{notAFailure} must not end the run");
                Assert.AreEqual(WorldEventOutcome.None, evt.Outcome, $"{notAFailure}");
            }
        }

        [TestMethod]
        public void IsObjectiveFailureOutcome_EveryOutcome()
        {
            var notFailures = new[] { WorldEventOutcome.None, WorldEventOutcome.Success, WorldEventOutcome.SuccessBossAbsent };

            foreach (WorldEventOutcome outcome in Enum.GetValues(typeof(WorldEventOutcome)))
                Assert.AreEqual(!notFailures.Contains(outcome), WorldEvent.IsObjectiveFailureOutcome(outcome), outcome.ToString());
        }

        [TestMethod]
        public void OnFinish_RunsForARunAbortedWhileIdle_WithNoOnStaged()
        {
            var objective = new HookedObjective();
            var evt = BuildEvent(objective);

            evt.Finish(WorldEventOutcome.AbortedAdmin);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.AreEqual(0, objective.StagedCalls.Count);
            Assert.AreEqual(1, objective.FinishCalls.Count);
            Assert.AreEqual(WorldEventOutcome.AbortedAdmin, objective.FinishCalls[0].Outcome);
        }

        [TestMethod]
        public void OnFinish_RunsWhenStageAbortsOnAFailedHold_WithNoOnStaged()
        {
            var objective = new HookedObjective();
            var evt = BuildEvent(objective, bridge: new FakeBridge { ReturnNull = true });

            evt.Stage(T0);

            Assert.AreEqual(WorldEventOutcome.AbortedError, evt.Outcome);
            Assert.AreEqual(0, objective.StagedCalls.Count);
            Assert.AreEqual(1, objective.FinishCalls.Count);
            Assert.AreEqual(WorldEventOutcome.AbortedError, objective.FinishCalls[0].Outcome);
        }

        // ---- the pure hold-reason rules ----------------------------------------------------------------

        [TestMethod]
        public void ComputeHoldReasons_OrsOneFlagPerInput()
        {
            Assert.AreEqual(WorldEventHoldReason.None, WorldEventLandblockHold.ComputeHoldReasons(false, false, false, false));
            Assert.AreEqual(WorldEventHoldReason.Source, WorldEventLandblockHold.ComputeHoldReasons(true, false, false, false));
            Assert.AreEqual(WorldEventHoldReason.Ring, WorldEventLandblockHold.ComputeHoldReasons(false, true, false, false));
            Assert.AreEqual(WorldEventHoldReason.Hazard, WorldEventLandblockHold.ComputeHoldReasons(false, false, true, false));
            Assert.AreEqual(WorldEventHoldReason.Objective, WorldEventLandblockHold.ComputeHoldReasons(false, false, false, true));
            Assert.AreEqual((WorldEventHoldReason)15, WorldEventLandblockHold.ComputeHoldReasons(true, true, true, true));
        }

        /// <summary>The pre-R4 boolean (source || ring || hazard) for all eight combinations, objective off.</summary>
        [TestMethod]
        public void HoldsAdjacents_MatchesThePreR4BooleanOr()
        {
            foreach (var source in new[] { false, true })
            foreach (var ring in new[] { false, true })
            foreach (var hazard in new[] { false, true })
            {
                var reasons = WorldEventLandblockHold.ComputeHoldReasons(source, ring, hazard, false);

                Assert.AreEqual(source || ring || hazard, WorldEventLandblockHold.HoldsAdjacents(reasons), $"source={source} ring={ring} hazard={hazard}");
                Assert.IsTrue(WorldEventLandblockHold.HoldsAdjacents(reasons | WorldEventHoldReason.Objective));
            }
        }

        [TestMethod]
        public void HoldReasonsLogSuffix_IsEmptyUnlessTheObjectiveIsAReason()
        {
            Assert.AreEqual("", WorldEventLandblockHold.HoldReasonsLogSuffix(WorldEventHoldReason.None));
            Assert.AreEqual("", WorldEventLandblockHold.HoldReasonsLogSuffix(WorldEventHoldReason.Source | WorldEventHoldReason.Ring | WorldEventHoldReason.Hazard));
            Assert.AreEqual(" reasons=objective", WorldEventLandblockHold.HoldReasonsLogSuffix(WorldEventHoldReason.Objective));
            Assert.AreEqual(" reasons=source+ring+hazard+objective", WorldEventLandblockHold.HoldReasonsLogSuffix((WorldEventHoldReason)15));
        }
    }
}
