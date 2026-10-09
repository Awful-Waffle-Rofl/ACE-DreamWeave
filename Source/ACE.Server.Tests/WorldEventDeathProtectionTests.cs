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

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for Asheron's Protection (WaffleACE) - the world-event death-penalty waiver. Tests
    /// only the pure core, <see cref="WorldEventManager.ProtectsDeathAtCore"/> (D6 - no PropertyManager, no
    /// live Player, no WorldEventManager.Current): state gating, radius containment, instance mismatch, and
    /// each disabled flag independently.
    /// </summary>
    [TestClass]
    public class WorldEventDeathProtectionTests
    {
        // An OUTDOOR cell in the middle of its landblock, so offsets within ~90m never cross a landblock
        // boundary and DistanceTo stays a plain 2D distance (mirrors WorldEventGeometryTests' fixture).
        private const uint OutdoorCell = 0x016C0025;

        private const float AnchorX = 96f;
        private const float AnchorY = 96f;
        private const float AnchorZ = 42f;

        private const uint TestInstance = 7u;
        private const uint OtherInstance = 9u;

        private const float TestRadius = 40f;

        private static Position Anchor()
        {
            return new Position(OutdoorCell, AnchorX, AnchorY, AnchorZ, 0f, 0f, 0f, 1f, TestInstance);
        }

        /// <summary>A position offset north of the anchor by <paramref name="metersNorth"/>, same instance unless overridden.</summary>
        private static Position At(float metersNorth, uint instance = TestInstance)
        {
            return new Position(OutdoorCell, AnchorX, AnchorY + metersNorth, AnchorZ, 0f, 0f, 0f, 1f, instance);
        }

        // ---- state gating --------------------------------------------------------------------------------

        [TestMethod]
        public void Active_WithinRadius_SameInstance_Protects()
        {
            Assert.IsTrue(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Announced_WithinRadius_SameInstance_Protects()
        {
            // Asheron's start line broadcasts at the Announced transition, up to AnnounceLeadSeconds before
            // Active - protection must already be live by then, or the line lies.
            Assert.IsTrue(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Announced,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Staged_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Staged,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Resolved_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Resolved,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Rewarding_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Rewarding,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Cleanup_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Cleanup,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Done_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Done,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void Idle_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Idle,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void NoCurrentRun_NullState_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, null,
                At(10f), Anchor(), TestRadius));
        }

        // ---- radius containment ---------------------------------------------------------------------------

        [TestMethod]
        public void WellInsideRadius_Protects()
        {
            Assert.IsTrue(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(5f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void WellOutsideRadius_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(TestRadius + 20f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void ExactlyAtRadiusBoundary_Protects()
        {
            // Sample() / ProtectsDeathAtCore both use DistanceTo(anchor) <= radius, so the boundary itself
            // counts as inside.
            Assert.IsTrue(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(TestRadius), Anchor(), TestRadius));
        }

        [TestMethod]
        public void JustPastRadiusBoundary_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(TestRadius + 0.5f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void ZeroRadius_NeverProtects()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                Anchor(), Anchor(), 0f));
        }

        // ---- instance mismatch ----------------------------------------------------------------------------

        [TestMethod]
        public void SamePosition_DifferentInstance_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(0f, OtherInstance), Anchor(), TestRadius));
        }

        // ---- disabled flags, independently ------------------------------------------------------------------

        [TestMethod]
        public void WorldEventsDisabled_DoesNotProtect_EvenIfDeathProtectionEnabled()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(false, true, WorldEventState.Active,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void DeathProtectionDisabled_DoesNotProtect_EvenIfWorldEventsEnabled()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, false, WorldEventState.Active,
                At(10f), Anchor(), TestRadius));
        }

        [TestMethod]
        public void BothFlagsDisabled_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(false, false, WorldEventState.Active,
                At(10f), Anchor(), TestRadius));
        }

        // ---- null safety ------------------------------------------------------------------------------------

        [TestMethod]
        public void NullLocation_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                null, Anchor(), TestRadius));
        }

        [TestMethod]
        public void NullAnchor_DoesNotProtect()
        {
            Assert.IsFalse(WorldEventManager.ProtectsDeathAtCore(true, true, WorldEventState.Active,
                At(10f), null, TestRadius));
        }

        // ---- announcer latch: start line gates the end line -------------------------------------------------
        //
        // Drives a real WorldEvent through Stage (Idle -> Staged -> Announced, which broadcasts the ordinary
        // start line and, when deathProtectionFlag is on, the Asheron's Protection start line) and then
        // Finish (-> Resolved, which broadcasts the outcome line and, ONLY if the start line went out, the
        // Asheron's Protection end line). No landblock bridge is supplied, so the hold and every spawn path
        // are skipped (WorldEventStateMachineTests' pattern, D6 - no test may stand up a world).

        private sealed class FakeObjective : IWorldEventObjective
        {
            public bool IsComplete { get; set; }

            public string ProgressText { get; set; } = "0 of 1 slain.";

            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now)
            {
            }

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private static readonly double T0 = 1_700_000_000d;

        private static SourceThemeDef BuildSource()
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
                HoldAdjacentLandblocks = false,
                RewardRadius = 60f,
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
                RuleKind = ACE.Server.WorldEvents.Defs.MvpRule.MostKills,
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
                ClaimWindowSeconds = 120,
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

        private static WorldEventComposition BuildComposition()
        {
            return new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, BossDef.None, BuildGoal(),
                BuildReward(), BuildAnchor(), new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                WorldEventAxisStore.Empty);
        }

        private static WorldEvent BuildEvent(Func<bool> deathProtectionFlag)
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                Invoker = "test"
            };

            return new WorldEvent(1, BuildComposition(), request, new FakeObjective(),
                () => new AudienceEstimate(3, 100, 150), () => T0,
                deathProtectionFlag: deathProtectionFlag);
        }

        [TestMethod]
        public void StartLineGoesOut_EndLineFollowsAtFinish()
        {
            var broadcasts = new List<string>();
            var previous = WorldEventAnnouncer.BroadcastSink;

            try
            {
                WorldEventAnnouncer.BroadcastSink = broadcasts.Add;

                var evt = BuildEvent(() => true);

                evt.Stage(T0);

                Assert.IsTrue(broadcasts.Any(m => m == WorldEventAnnouncer.DeathProtectionStartLine),
                    "start line was not broadcast");

                evt.Finish(WorldEventOutcome.Success);

                Assert.IsTrue(broadcasts.Any(m => m == WorldEventAnnouncer.DeathProtectionEndLine),
                    "end line was not broadcast after a start line went out");
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = previous;
            }
        }

        [TestMethod]
        public void DeathProtectionDisabled_NeitherStartNorEndLineIsBroadcast()
        {
            var broadcasts = new List<string>();
            var previous = WorldEventAnnouncer.BroadcastSink;

            try
            {
                WorldEventAnnouncer.BroadcastSink = broadcasts.Add;

                var evt = BuildEvent(() => false);

                evt.Stage(T0);

                Assert.IsFalse(broadcasts.Any(m => m == WorldEventAnnouncer.DeathProtectionStartLine),
                    "start line must not be broadcast while world_event_death_protection is off");

                evt.Finish(WorldEventOutcome.Success);

                Assert.IsFalse(broadcasts.Any(m => m == WorldEventAnnouncer.DeathProtectionEndLine),
                    "end line must never be broadcast without a matching start line");
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = previous;
            }
        }
    }
}
