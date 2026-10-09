using System;
using System.Collections.Generic;

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
    /// Unit coverage for the World Events -> Discord relay (2026-09-05): the opt-in
    /// <see cref="WorldEventAnnouncer.Broadcast"/> relayToDiscord parameter, its fail-open contract, and
    /// - the point of the whole feature - WHICH beats of a live run actually reach Discord.
    ///
    /// That last one is the invariant worth a test rather than a comment: the ask was the teaser, the run
    /// going live, and the outcome, explicitly NOT "every progress update along the way". A future call
    /// site that passes relayToDiscord: true on the 30s Announced line or a countdown warning turns the
    /// Discord channel into spam, and nothing else in the build would catch it.
    ///
    /// Follows <see cref="WorldEventTeaserTests"/>'s harness conventions - directly-constructed
    /// WorldEvents through the seams, no database/landblock/live session anywhere (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventDiscordRelayTests
    {
        private static readonly double T0 = 1_700_000_000d;

        // ---- fakes and builders (mirrors WorldEventTeaserTests) -------------------------------------

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

        private const string TeaserFlavour = "A low rumble can be felt coming from {anchor}.";
        private const string StartFlavour = "Something is coming through the weave near {anchor}.";

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
                StartFlavour = StartFlavour,
                WaveFlavour = "Another rank tears its way in!",
                TeaserFlavour = TeaserFlavour
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

        private static WorldEvent BuildEvent(Func<int> teaserLeadSource)
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                Invoker = "test"
            };

            var composition = new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, BossDef.None,
                BuildGoal(), BuildReward(), BuildAnchor(),
                new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);

            return new WorldEvent(1, composition, request, new FakeObjective(),
                () => new AudienceEstimate(3, 100, 150), () => T0, teaserLeadSource: teaserLeadSource);
        }

        /// <summary>
        /// Swaps BOTH announcer sinks for the duration of <paramref name="body"/> and restores them in a
        /// finally, so a failing assertion can never leak a captured sink into another test.
        /// </summary>
        private static void WithCapturedSinks(Action<List<string>, List<string>> body,
            Action<string> relaySinkOverride = null)
        {
            var broadcasts = new List<string>();
            var relayed = new List<string>();

            var previousBroadcast = WorldEventAnnouncer.BroadcastSink;
            var previousRelay = WorldEventAnnouncer.DiscordRelaySink;

            try
            {
                WorldEventAnnouncer.BroadcastSink = broadcasts.Add;
                WorldEventAnnouncer.DiscordRelaySink = relaySinkOverride ?? relayed.Add;

                body(broadcasts, relayed);
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = previousBroadcast;
                WorldEventAnnouncer.DiscordRelaySink = previousRelay;
            }
        }

        private static bool ContainsSubstring(IEnumerable<string> lines, string needle)
        {
            foreach (var line in lines)
            {
                if (line != null && line.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        // ---- Broadcast's relayToDiscord parameter ----------------------------------------------------

        [TestMethod]
        public void Broadcast_WithoutTheFlag_DoesNotRelay()
        {
            WithCapturedSinks((broadcasts, relayed) =>
            {
                WorldEventAnnouncer.Broadcast("[World Event] 30 seconds remain!");

                Assert.AreEqual(1, broadcasts.Count, "the in-game send must still happen");
                Assert.AreEqual(0, relayed.Count, "relaying is opt-in, so the default must stay out of Discord");
            });
        }

        [TestMethod]
        public void Broadcast_WithTheFlag_RelaysTheSameLineVerbatim()
        {
            const string line = "[World Event] Kill Count at the test anchor succeeded!";

            WithCapturedSinks((broadcasts, relayed) =>
            {
                WorldEventAnnouncer.Broadcast(line, relayToDiscord: true);

                CollectionAssert.AreEqual(new List<string> { line }, broadcasts);
                CollectionAssert.AreEqual(new List<string> { line }, relayed,
                    "Discord must show exactly what players saw, prefix included");
            });
        }

        [TestMethod]
        public void Broadcast_EmptyMessage_ReachesNeitherSink()
        {
            WithCapturedSinks((broadcasts, relayed) =>
            {
                WorldEventAnnouncer.Broadcast("", relayToDiscord: true);
                WorldEventAnnouncer.Broadcast(null, relayToDiscord: true);

                Assert.AreEqual(0, broadcasts.Count);
                Assert.AreEqual(0, relayed.Count);
            });
        }

        /// <summary>
        /// The relay is best-effort: a fault in it must never cost players the in-game announcement, which
        /// is why the in-game send runs FIRST and the relay is wrapped. Without the wrapper this test
        /// throws out of Broadcast.
        /// </summary>
        [TestMethod]
        public void Broadcast_RelaySinkThrows_DoesNotPropagateAndTheInGameSendStillHappened()
        {
            var threw = 0;

            WithCapturedSinks((broadcasts, relayed) =>
            {
                WorldEventAnnouncer.Broadcast("[World Event] boom", relayToDiscord: true);

                Assert.AreEqual(1, broadcasts.Count, "the in-game send must survive a broken relay");
                Assert.AreEqual(1, threw, "the failing relay sink must actually have been reached");
            },
            relaySinkOverride: _ =>
            {
                threw++;
                throw new InvalidOperationException("webhook exploded");
            });
        }

        // ---- which beats of a real run reach Discord --------------------------------------------------

        /// <summary>
        /// Drives a run from Begin through the teaser lead into Active and asserts the split: three
        /// globals go out in game (teaser, the Announced StartLine, the Active line) and exactly TWO of
        /// them relay. The StartLine is deliberately not one of them - it fires 30s (here 0s) before the
        /// Active line and would read as a duplicate in Discord.
        /// </summary>
        [TestMethod]
        public void RunLifecycle_RelaysTheTeaserAndTheActiveLineOnly()
        {
            WithCapturedSinks((broadcasts, relayed) =>
            {
                var evt = BuildEvent(teaserLeadSource: () => 180);

                evt.Begin(T0);

                Assert.AreEqual(1, relayed.Count, "the teaser is the first thing Discord hears about");

                // Past the teaser lead: Stage() -> Announced (StartLine) -> Active (ActiveLine). Ticked
                // more than once because the state machine advances at most one step per tick.
                evt.Tick(T0 + 180);
                evt.Tick(T0 + 181);
                evt.Tick(T0 + 182);

                Assert.AreEqual(WorldEventState.Active, evt.State, "the run must have reached Active");

                Assert.IsTrue(ContainsSubstring(broadcasts, "A low rumble can be felt coming from"),
                    "the teaser must have gone out in game");
                Assert.IsTrue(ContainsSubstring(broadcasts, "Something is coming through the weave near"),
                    "the Announced StartLine must have gone out in game");
                Assert.IsTrue(ContainsSubstring(broadcasts, "Kill Count at"),
                    "the Active line must have gone out in game");

                Assert.AreEqual(2, relayed.Count,
                    $"exactly the teaser and the Active line relay; got: {string.Join(" | ", relayed)}");

                Assert.IsTrue(ContainsSubstring(relayed, "A low rumble can be felt coming from"),
                    "the teaser must relay");
                Assert.IsTrue(ContainsSubstring(relayed, "Kill Count at"),
                    "the Active line must relay");
                Assert.IsFalse(ContainsSubstring(relayed, "Something is coming through the weave near"),
                    "the Announced StartLine must NOT relay - it duplicates the Active line 30s later");
            });
        }

        /// <summary>
        /// AbortedShutdown fires for every in-flight run on every routine server restart. When the run had
        /// not yet reached a relaying beat, Discord was never told the event was coming, so it must not be
        /// told the event was cancelled - a deploy that happens to overlap a staged run would otherwise
        /// post a cancellation for something that channel never announced.
        /// </summary>
        [TestMethod]
        public void AbortBeforeAnythingRelayed_DoesNotRelayTheCancellation()
        {
            WithCapturedSinks((broadcasts, relayed) =>
            {
                // Zero lead: Begin stages straight through with no teaser, so nothing has relayed yet.
                var evt = BuildEvent(teaserLeadSource: () => 0);

                evt.Begin(T0);

                Assert.AreEqual(0, relayed.Count, "a zero-lead run has not relayed anything yet");

                evt.Finish(WorldEventOutcome.AbortedShutdown);

                Assert.IsTrue(ContainsSubstring(broadcasts, "was cancelled"),
                    "players in game still see the cancellation");
                Assert.AreEqual(0, relayed.Count,
                    $"Discord never heard of this run, so it must not hear it was cancelled; got: {string.Join(" | ", relayed)}");
            });
        }

        /// <summary>
        /// The other half of the same rule: once the teaser HAS gone to Discord, readers who were told
        /// something was coming must be told it is not. Otherwise they travel to an anchor for nothing.
        /// </summary>
        [TestMethod]
        public void AbortAfterTheTeaserRelayed_RelaysTheCancellation()
        {
            WithCapturedSinks((broadcasts, relayed) =>
            {
                var evt = BuildEvent(teaserLeadSource: () => 180);

                evt.Begin(T0);

                Assert.AreEqual(1, relayed.Count, "the teaser relayed");

                evt.Finish(WorldEventOutcome.AbortedAdmin);

                Assert.AreEqual(2, relayed.Count,
                    $"the cancellation must follow the teaser to Discord; got: {string.Join(" | ", relayed)}");
                Assert.IsTrue(ContainsSubstring(relayed, "was cancelled"));
            });
        }

        /// <summary>
        /// The countdown warnings are the clearest "progress update along the way" the run emits, and they
        /// are the regression this guards: WarnLine goes through the same Broadcast, so relaying it is a
        /// one-word change away.
        /// </summary>
        [TestMethod]
        public void WarnLines_DoNotRelay()
        {
            WithCapturedSinks((broadcasts, relayed) =>
            {
                WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.WarnLine(60));
                WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.WarnLine(30));

                Assert.AreEqual(2, broadcasts.Count);
                Assert.AreEqual(0, relayed.Count);
            });
        }
    }
}
