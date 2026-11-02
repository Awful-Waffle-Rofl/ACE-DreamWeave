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
    /// Presence payout wiring: the claim decision (WorldEventClaimRules.ResolvePresencePayout and its player text
    /// and log suffix), the crate-opening pure helpers (WorldEventRewardCrate), the arena rule
    /// (WorldEvent.BuildArena) and the run's presence window (Begin at Active, a sample per Active tick, close on
    /// the first complete tick or at Finish, the death grace). No PropertyManager read: every setting the run
    /// would read is supplied through its test seam.
    /// </summary>
    [TestClass]
    public class WorldEventPresencePayoutTests
    {
        // ---- ResolvePresencePayout ---------------------------------------------------------------------

        private const uint Coal = 1003850;

        [TestMethod]
        public void SwitchOff_UsesTheCreditRule_AndStampsNothing()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(false, Coal, true, 0.0, true);

            Assert.IsTrue(r.UsesCreditRule);
            Assert.IsFalse(r.PayCoal, "the credit rule, not presence, decides coal with the switch off");
            Assert.IsFalse(r.StampMultiplier);
            Assert.IsNull(WorldEventClaimRules.PresenceMessage(r));
            Assert.AreEqual("", Suffix(r, payCoal: false, success: true), "the claim line is unchanged with the switch off");
        }

        [TestMethod]
        public void KnownZero_WithCoal_PaysCoal()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.0, true);

            Assert.IsFalse(r.UsesCreditRule);
            Assert.IsTrue(r.PayCoal);
            Assert.IsFalse(r.StampMultiplier);
            Assert.AreEqual("You were never inside the event ring, so you receive a Non Participation Award.", WorldEventClaimRules.PresenceMessage(r));
            Assert.AreEqual(" presence=0 mult=none", Suffix(r, payCoal: true, success: true), "coal stamps nothing");
        }

        [TestMethod]
        public void KnownZero_WithNoCoalWcid_PaysAnUnscaledCrate()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, 0, true, 0.0, true);

            Assert.IsFalse(r.PayCoal, "an axis with no coal wcid has no booby prize");
            Assert.IsTrue(r.StampMultiplier);
            Assert.AreEqual(1.0, r.Multiplier, 1e-9, "a zero-roll crate would never open, so absence reads as the unscaled crate");
            Assert.IsNull(WorldEventClaimRules.PresenceMessage(r), "review F4: no 'present for 0%' line on a crate that is not coal");
        }

        [TestMethod]
        public void Unknown_PaysTheCrateAtOne_NeverCoal()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, false, 0.0, true);

            Assert.IsFalse(r.PayCoal);
            Assert.IsTrue(r.StampMultiplier);
            Assert.AreEqual(1.0, r.Multiplier, 1e-9);
            Assert.IsFalse(r.Known);
            Assert.IsNull(WorldEventClaimRules.PresenceMessage(r), "no percentage is claimed for an unknown presence");
            Assert.AreEqual(" presence=unknown mult=1", Suffix(r, payCoal: false, success: true));
        }

        [TestMethod]
        public void Unknown_NaNFraction_IsTreatedAsUnknown()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, double.NaN, true);

            Assert.IsFalse(r.PayCoal);
            Assert.AreEqual(1.0, r.Multiplier, 1e-9);
        }

        [DataTestMethod]
        [DataRow(0.25, 0.5, "You were present for 25% of the event. Your Event Crate carries a 0.5x reward.", " presence=25 mult=0.5")]
        [DataRow(0.5, 1.0, "You were present for 50% of the event. Your Event Crate carries a 1x reward.", " presence=50 mult=1")]
        [DataRow(1.0, 2.0, "You were present for 100% of the event. Your Event Crate carries a 2x reward.", " presence=100 mult=2")]
        public void KnownPresence_ScalesTheCrateAtTwoP(double p, double m, string message, string suffix)
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, p, true);

            Assert.IsFalse(r.PayCoal);
            Assert.IsTrue(r.StampMultiplier);
            Assert.AreEqual(m, r.Multiplier, 1e-9);
            Assert.AreEqual(message, WorldEventClaimRules.PresenceMessage(r));
            Assert.AreEqual(suffix, Suffix(r, payCoal: false, success: true));
        }

        [TestMethod]
        public void KnownPresence_IsClampedToOne()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 1.7, true);

            Assert.AreEqual(2.0, r.Multiplier, 1e-9, "m never exceeds 2");
        }

        [TestMethod]
        public void FailedRun_ConsolationCrate_IsNotScaled_ButZeroStillPaysCoal()
        {
            var present = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.25, false);

            Assert.IsFalse(present.PayCoal);
            Assert.IsFalse(present.StampMultiplier, "the consolation crate is unchanged");
            Assert.AreEqual(1.0, present.Multiplier, 1e-9);
            Assert.IsNull(WorldEventClaimRules.PresenceMessage(present), "no 'Event Crate carries' line for a consolation crate");

            var absent = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.0, false);

            Assert.IsTrue(absent.PayCoal, "one consolation crate per PRESENT claimant: an absent one gets coal");
        }

        // ---- ResolveStamp (review F3) ----------------------------------------------------------------------

        private static string Suffix(PresencePayout r, bool payCoal, bool success)
            => WorldEventClaimRules.PresenceLogSuffix(r, WorldEventClaimRules.ResolveStamp(r, payCoal, success));

        [TestMethod]
        public void Stamp_SwitchOff_StampsNothing()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(false, Coal, true, 0.5, true);

            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(r, payCoal: false, paysSuccess: true));
            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(r, payCoal: true, paysSuccess: true));
        }

        [TestMethod]
        public void Stamp_SuccessCrate_CarriesTwoP()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.3, true);

            var s = WorldEventClaimRules.ResolveStamp(r, payCoal: false, paysSuccess: true);

            Assert.IsTrue(s.Stamp);
            Assert.AreEqual(0.6, s.Multiplier, 1e-9);
        }

        [TestMethod]
        public void Stamp_ConsolationCrate_StampsNothing_AndLogsMultNone()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.25, false);

            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(r, payCoal: false, paysSuccess: false));
            Assert.AreEqual(" presence=25 mult=none", Suffix(r, payCoal: false, success: false), "a consolation crate never logs mult=1");
        }

        [TestMethod]
        public void Stamp_CoalPaid_StampsNothing()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.0, true);

            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(r, payCoal: true, paysSuccess: true));
        }

        [TestMethod]
        public void Stamp_CoalFellBackToTheSuccessCrate_CarriesOne()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.0, true);

            Assert.AreEqual((true, 1.0), WorldEventClaimRules.ResolveStamp(r, payCoal: false, paysSuccess: true));
            Assert.AreEqual(" presence=0 mult=1", Suffix(r, payCoal: false, success: true));
        }

        [TestMethod]
        public void Stamp_CoalFellBackToTheConsolationCrate_StampsNothing()
        {
            var r = WorldEventClaimRules.ResolvePresencePayout(true, Coal, true, 0.0, false);

            Assert.AreEqual((false, 0.0), WorldEventClaimRules.ResolveStamp(r, payCoal: false, paysSuccess: false));
            Assert.AreEqual(" presence=0 mult=none", Suffix(r, payCoal: false, success: false));
        }

        [TestMethod]
        public void PresencePercent_RoundsHalfAwayFromZero_AndClamps()
        {
            Assert.AreEqual(13, WorldEventClaimRules.PresencePercent(0.125));
            Assert.AreEqual(0, WorldEventClaimRules.PresencePercent(-0.5));
            Assert.AreEqual(100, WorldEventClaimRules.PresencePercent(1.5));
            Assert.AreEqual(0, WorldEventClaimRules.PresencePercent(double.NaN));
        }

        // ---- WorldEventRewardCrate pure helpers -----------------------------------------------------------

        [TestMethod]
        public void Crate_Multiplier_MissingOrInvalidReadsAsOne_AndClampsAtTwo()
        {
            Assert.AreEqual(1.0, WorldEventRewardCrate.ResolveMultiplier(null), 1e-9);
            Assert.AreEqual(1.0, WorldEventRewardCrate.ResolveMultiplier(double.NaN), 1e-9);
            Assert.AreEqual(1.0, WorldEventRewardCrate.ResolveMultiplier(0.0), 1e-9);
            Assert.AreEqual(1.0, WorldEventRewardCrate.ResolveMultiplier(-1.0), 1e-9);
            Assert.AreEqual(0.4, WorldEventRewardCrate.ResolveMultiplier(0.4), 1e-9);
            Assert.AreEqual(2.0, WorldEventRewardCrate.ResolveMultiplier(9.0), 1e-9);
        }

        [TestMethod]
        public void Crate_Profile_MissingReadsAsStandard()
        {
            Assert.AreEqual("standard", WorldEventRewardCrate.ResolveProfile(null));
            Assert.AreEqual("standard", WorldEventRewardCrate.ResolveProfile("  "));
            Assert.AreEqual("elite", WorldEventRewardCrate.ResolveProfile(" elite "));
        }

        [TestMethod]
        public void Crate_SplitStacks_HonoursMaxStackSize_AndSplitsNonStackables()
        {
            CollectionAssert.AreEqual(new[] { 25 }, WorldEventRewardCrate.SplitStacks(25, 1000));
            CollectionAssert.AreEqual(new[] { 100, 100, 50 }, WorldEventRewardCrate.SplitStacks(250, 100));
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, WorldEventRewardCrate.SplitStacks(3, 1), "a non-stackable item is one object per unit");
            Assert.AreEqual(0, WorldEventRewardCrate.SplitStacks(0, 100).Count);
        }

        // ---- BuildArena ---------------------------------------------------------------------------------

        private static Position Outdoor(float x, float y) => new Position(0xA9B40005, x, y, 50f, 0f, 0f, 0f, 1f, 0);

        [TestMethod]
        public void BuildArena_ZeroRadiusUsesRewardRadius_AndZeroHalfHeightIsHalfTheRadius()
        {
            var theme = new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef() };

            var arena = WorldEvent.BuildArena(theme, Outdoor(96f, 96f));

            Assert.AreEqual(60f, arena.Radius, 1e-4);
            Assert.AreEqual(30f, arena.HalfHeight, 1e-4);
        }

        [TestMethod]
        public void BuildArena_ExplicitValuesWin()
        {
            var theme = new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef { Radius = 25f, HalfHeight = 8f } };

            var arena = WorldEvent.BuildArena(theme, Outdoor(96f, 96f));

            Assert.AreEqual(25f, arena.Radius, 1e-4);
            Assert.AreEqual(8f, arena.HalfHeight, 1e-4);
        }

        [TestMethod]
        public void BuildArena_NoArenaBlock_StillTracksOnTheRewardRadius()
        {
            var arena = WorldEvent.BuildArena(new SourceThemeDef { RewardRadius = 40f }, Outdoor(96f, 96f));

            Assert.IsNotNull(arena);
            Assert.AreEqual(40f, arena.Radius, 1e-4);
            Assert.AreEqual(20f, arena.HalfHeight, 1e-4);
        }

        [TestMethod]
        public void BuildArena_NoAnchorOrNoRadius_IsNull()
        {
            Assert.IsNull(WorldEvent.BuildArena(new SourceThemeDef { RewardRadius = 60f }, null));
            Assert.IsNull(WorldEvent.BuildArena(new SourceThemeDef { RewardRadius = 0f }, Outdoor(96f, 96f)));
        }

        [TestMethod]
        public void BuildArena_NullGoalRadius_MatchesTheThemeRuleTable()
        {
            // Expected values derived by hand from origin/master's formula (0386f34e9 WorldEvent.BuildArena):
            //   radius = arena.Radius > 0 ? arena.Radius : rewardRadius; null unless 0 < radius < infinity;
            //   halfHeight = arena.HalfHeight > 0 ? arena.HalfHeight : radius / 2.
            var cases = new (string Label, SourceThemeDef Theme, float? Radius, float? HalfHeight)[]
            {
                ("null theme",                     null,                                                                                   null, null),
                ("empty theme",                    new SourceThemeDef(),                                                                   null, null),
                ("reward 40",                      new SourceThemeDef { RewardRadius = 40f },                                              40f,  20f),
                ("reward 0",                       new SourceThemeDef { RewardRadius = 0f },                                               null, null),
                ("reward -10",                     new SourceThemeDef { RewardRadius = -10f },                                             null, null),
                ("reward +inf",                    new SourceThemeDef { RewardRadius = float.PositiveInfinity },                           null, null),
                ("reward NaN",                     new SourceThemeDef { RewardRadius = float.NaN },                                        null, null),
                ("reward 60, empty arena",         new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef() },                      60f,  30f),
                ("reward 60, arena 25/8",          new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef { Radius = 25f, HalfHeight = 8f } }, 25f, 8f),
                ("reward 60, arena -3/-1",         new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef { Radius = -3f, HalfHeight = -1f } }, 60f, 30f),
                ("reward 60, arena 0/5",           new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef { HalfHeight = 5f } },   60f,  5f),
                ("reward 0, arena 12",             new SourceThemeDef { RewardRadius = 0f, Arena = new ArenaDef { Radius = 12f } },       12f,  6f),
                ("reward 0, arena +inf",           new SourceThemeDef { RewardRadius = 0f, Arena = new ArenaDef { Radius = float.PositiveInfinity } }, null, null),
            };

            var anchor = Outdoor(96f, 96f);

            foreach (var c in cases)
            {
                foreach (var (how, arena) in new[] { ("3-arg null goal", WorldEvent.BuildArena(c.Theme, null, anchor)), ("2-arg", WorldEvent.BuildArena(c.Theme, anchor)) })
                {
                    var label = $"{c.Label} ({how})";

                    if (c.Radius == null)
                    {
                        Assert.IsNull(arena, label);
                        continue;
                    }

                    Assert.IsNotNull(arena, label);
                    Assert.AreEqual(c.Radius.Value, arena.Radius, 1e-4, label);
                    Assert.AreEqual(c.HalfHeight.Value, arena.HalfHeight, 1e-4, label);
                    Assert.AreEqual(anchor.LandblockId.Raw, arena.Centre.LandblockId.Raw, label);
                    Assert.AreEqual(anchor.Pos, arena.Centre.Pos, label);
                    Assert.AreNotSame(anchor, arena.Centre, label + ": the centre is a copy");
                }

                Assert.IsNull(WorldEvent.BuildArena(c.Theme, null, null), c.Label + ": no anchor");
            }
        }

        [TestMethod]
        public void ComposeInside_IsInsideAndNotIdle()
        {
            Assert.IsTrue(WorldEvent.ComposeInside(arenaInside: true, idle: false));
            Assert.IsFalse(WorldEvent.ComposeInside(arenaInside: true, idle: true));
            Assert.IsFalse(WorldEvent.ComposeInside(arenaInside: false, idle: false));
            Assert.IsFalse(WorldEvent.ComposeInside(arenaInside: false, idle: true));
        }

        [TestMethod]
        public void BuildArena_GoalRadius_WinsOverTheTheme_AndKeepsTheThemeHalfHeight()
        {
            var explicitTheme = new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef { Radius = 25f, HalfHeight = 8f } };

            var arena = WorldEvent.BuildArena(explicitTheme, 45f, Outdoor(96f, 96f));

            Assert.AreEqual(45f, arena.Radius, 1e-4);
            Assert.AreEqual(8f, arena.HalfHeight, 1e-4, "an explicit theme halfHeight still applies");

            var bare = WorldEvent.BuildArena(new SourceThemeDef { RewardRadius = 60f }, 30f, Outdoor(96f, 96f));

            Assert.AreEqual(30f, bare.Radius, 1e-4);
            Assert.AreEqual(15f, bare.HalfHeight, 1e-4, "no theme halfHeight: radius / 2 of the goal radius");

            var noTheme = WorldEvent.BuildArena(null, 20f, Outdoor(96f, 96f));

            Assert.IsNotNull(noTheme, "a goal radius needs no theme");
            Assert.AreEqual(20f, noTheme.Radius, 1e-4);
        }

        [TestMethod]
        public void BuildArena_UnsetGoalRadius_FallsBackToTheTheme_AndNoAnchorStillWins()
        {
            var bare = new SourceThemeDef { RewardRadius = 60f };
            var explicitTheme = new SourceThemeDef { RewardRadius = 60f, Arena = new ArenaDef { Radius = 25f, HalfHeight = 8f } };

            foreach (var unset in new float?[] { null, 0f, -5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var label = $"goal={(unset?.ToString() ?? "null")}";

                var a = WorldEvent.BuildArena(bare, unset, Outdoor(96f, 96f));

                Assert.IsNotNull(a, label + ": an unset goal radius never disables the arena");
                Assert.AreEqual(60f, a.Radius, 1e-4, label);
                Assert.AreEqual(30f, a.HalfHeight, 1e-4, label);

                var b = WorldEvent.BuildArena(explicitTheme, unset, Outdoor(96f, 96f));

                Assert.AreEqual(25f, b.Radius, 1e-4, label);
                Assert.AreEqual(8f, b.HalfHeight, 1e-4, label);

                // No theme radius and no goal radius: still no arena, as before.
                Assert.IsNull(WorldEvent.BuildArena(new SourceThemeDef(), unset, Outdoor(96f, 96f)), label);
            }

            Assert.IsNull(WorldEvent.BuildArena(bare, 30f, null), "no anchor is still no arena");
        }

        // ---- the run's presence window ----------------------------------------------------------------------

        private sealed class FakeObjective : IWorldEventObjective
        {
            public bool IsComplete { get; set; }

            public string ProgressText => "";

            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now)
            {
            }

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private const double T0 = 1_700_000_000d;

        private const uint Alice = 0x50000001;
        private const uint Bob = 0x50000002;

        private sealed class Harness
        {
            public double Clock = T0;
            public FakeObjective Objective = new FakeObjective();
            public List<PresenceSample> Samples = new List<PresenceSample>();
            public int SampleCalls;
            public WorldEvent Event;
        }

        private static Harness Build(Position anchor, ArenaDef arena = null, bool ring = false, int minDurationSeconds = WorldEvent.DefaultMinDurationSeconds)
        {
            var h = new Harness();

            var source = new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "single",
                GeometryKind = SourceGeometry.Single,
                GeometryRadius = 0f,
                GeometryPoints = 1,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1, Cap = 18 },
                RewardRadius = 60f,
                CompatibleGoals = new List<string> { "kill_count" },
                Arena = arena
            };

            var goal = new GoalDef
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

            var reward = new RewardDef
            {
                Id = "standard",
                DisplayName = "Event Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = 120
            };

            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string>(),
                Members = new List<FamilyMember> { new FamilyMember { Wcid = 1002604, Name = "Thrall", Level = 20, Role = 0 } }
            };

            var anchorDef = new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = anchor.LandblockId.Raw, BiomeTags = new List<string>() };

            var composition = new WorldEventComposition(source, new[] { family }, BossDef.None, goal, reward, anchorDef, anchor, WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                MinDurationSeconds = minDurationSeconds,
                Invoker = "test"
            };

            h.Event = new WorldEvent(1, composition, request, h.Objective,
                audienceSampler: () => new AudienceEstimate(3, 100, 150),
                clock: () => h.Clock,
                presenceCounter: () => 1,
                presenceSampler: () =>
                {
                    h.SampleCalls++;
                    return h.Samples.ToList();
                },
                presenceSettings: () => (120, 2),
                ringFlag: () => ring);

            return h;
        }

        private static Position Indoor() => new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0);

        /// <summary>Stage, then the Announced -> Active edge at T0 (announce lead 0).</summary>
        private static void GoActive(Harness h)
        {
            h.Event.Stage(T0);
            h.Event.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, h.Event.State, "fixture: the run is Active");
        }

        private static void TickAt(Harness h, double t)
        {
            h.Clock = t;
            h.Event.Tick(t);
        }

        [TestMethod]
        public void Presence_IsBuiltAtStage_AndOpensAtActive()
        {
            var h = Build(Indoor());

            h.Event.Stage(T0);

            Assert.IsNotNull(h.Event.Arena);
            Assert.IsNotNull(h.Event.Presence);
            Assert.IsFalse(h.Event.Presence.TryGetFraction(Alice, out _), "the window has not opened before Active");

            h.Event.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, h.Event.State);
        }

        [TestMethod]
        public void ActiveTicks_AccruePresence_ForPlayersInside()
        {
            var h = Build(Indoor());
            GoActive(h);

            h.Samples.Add(new PresenceSample(Alice, "Alice", true));
            h.Samples.Add(new PresenceSample(Bob, "Bob", false));

            for (var t = 1; t <= 100; t++)
                TickAt(h, T0 + t);

            Assert.IsTrue(h.Event.Presence.TryGetFraction(Alice, out var pa));
            Assert.AreEqual(1.0, pa, 1e-9);
            Assert.IsTrue(h.Event.Presence.TryGetFraction(Bob, out var pb));
            Assert.AreEqual(0.0, pb, 1e-9);
        }

        [TestMethod]
        public void Window_Closes_OnTheFirstCompleteTick_EvenWhileTheMinimumHoldsTheRunOpen()
        {
            var h = Build(Indoor(), minDurationSeconds: 300);
            GoActive(h);

            h.Samples.Add(new PresenceSample(Alice, "Alice", true));

            for (var t = 1; t <= 50; t++)
                TickAt(h, T0 + t);

            h.Objective.IsComplete = true;
            TickAt(h, T0 + 51);

            Assert.AreEqual(WorldEventState.Active, h.Event.State, "fixture: the minimum duration holds the run open");

            var closedTotal = h.Event.Presence.TotalSeconds;

            // Bob turns up after the objective is complete: nothing he does counts.
            h.Samples.Clear();
            h.Samples.Add(new PresenceSample(Bob, "Bob", true));

            for (var t = 52; t <= 120; t++)
                TickAt(h, T0 + t);

            Assert.AreEqual(closedTotal, h.Event.Presence.TotalSeconds, 1e-9, "no time accrues after the close");
            Assert.IsTrue(h.Event.Presence.TryGetFraction(Alice, out var pa));
            Assert.AreEqual(1.0, pa, 1e-9);
            Assert.IsTrue(h.Event.Presence.TryGetFraction(Bob, out var pb));
            Assert.AreEqual(0.0, pb, 1e-9, "a late arrival after completion was never present");
        }

        [TestMethod]
        public void Window_Closes_AtFinish_WhenTheObjectiveNeverCompletes()
        {
            var h = Build(Indoor());
            GoActive(h);

            h.Samples.Add(new PresenceSample(Alice, "Alice", true));

            for (var t = 1; t <= 10; t++)
                TickAt(h, T0 + t);

            h.Clock = T0 + 12;
            h.Event.Finish(WorldEventOutcome.FailedTimeout);

            var total = h.Event.Presence.TotalSeconds;

            Assert.AreEqual(12.0, total, 1e-9, "Finish takes a closing sample that credits the span since the last tick");

            h.Event.Tick(T0 + 20);

            Assert.AreEqual(total, h.Event.Presence.TotalSeconds, 1e-9, "nothing accrues after Finish");
        }

        [TestMethod]
        public void TheClosingTick_DoesNotWalkTheOnlineListTwice()
        {
            var h = Build(Indoor(), minDurationSeconds: 1);
            GoActive(h);

            TickAt(h, T0 + 1);
            var before = h.SampleCalls;

            h.Objective.IsComplete = true;
            TickAt(h, T0 + 2);

            Assert.AreEqual(WorldEventState.Rewarding, h.Event.State, "fixture: the objective finished the run on this tick");
            Assert.AreEqual(before + 1, h.SampleCalls, "one walk per tick, including the tick that closes the window and finishes the run");
        }

        [TestMethod]
        public void DeathInsideTheArena_StartsTheGrace()
        {
            var h = Build(Indoor());
            GoActive(h);

            h.Samples.Add(new PresenceSample(Alice, "Alice", true));

            for (var t = 1; t <= 50; t++)
                TickAt(h, T0 + t);

            h.Event.OnPlayerDied(Alice, Indoor());

            // The corpse lies inside for the death animation and the lifestone delay first.
            for (var t = 51; t <= 55; t++)
                TickAt(h, T0 + t);

            // Respawned at the lifestone: outside, but within the 120 s grace.
            h.Samples.Clear();
            h.Samples.Add(new PresenceSample(Alice, "Alice", false));

            for (var t = 56; t <= 100; t++)
                TickAt(h, T0 + t);

            Assert.IsTrue(h.Event.Presence.TryGetFraction(Alice, out var pa));
            Assert.AreEqual(1.0, pa, 1e-9, "the corpse run inside the grace still counts");
            Assert.AreEqual(1, h.Event.Presence.GraceDeaths);
        }

        [TestMethod]
        public void DeathOutsideTheArena_StartsNoGrace()
        {
            var h = Build(Indoor());
            GoActive(h);

            h.Samples.Add(new PresenceSample(Alice, "Alice", true));

            for (var t = 1; t <= 50; t++)
                TickAt(h, T0 + t);

            // Same spot, 500 m below: outside the arena's 30 m half-height.
            h.Event.OnPlayerDied(Alice, new Position(0x016C019E, 10f, 10f, -500f, 0f, 0f, 0f, 1f, 0));

            h.Samples.Clear();
            h.Samples.Add(new PresenceSample(Alice, "Alice", false));

            for (var t = 51; t <= 100; t++)
                TickAt(h, T0 + t);

            Assert.IsTrue(h.Event.Presence.TryGetFraction(Alice, out var pa));
            Assert.AreEqual(0.5, pa, 0.02);
            Assert.AreEqual(0, h.Event.Presence.GraceDeaths);
        }

        [TestMethod]
        public void DeathBeforeActive_IsIgnored()
        {
            var h = Build(Indoor());
            h.Event.Stage(T0);

            h.Event.OnPlayerDied(Alice, Indoor());

            Assert.AreEqual(0, h.Event.Presence.GraceDeaths);
        }

        [TestMethod]
        public void Ring_HoldsAdjacents_OnlyWhenItIsPlanned_AndTheCircleCrossesAnEdge()
        {
            var arena = new ArenaDef { RingWcid = 1002672 };

            var edge = Build(Outdoor(5f, 96f), arena, ring: true);
            edge.Event.Stage(T0);
            Assert.IsTrue(edge.Event.RingHoldsAdjacents, "a 60 m circle 5 m from the west edge crosses it");

            var centre = Build(Outdoor(96f, 96f), new ArenaDef { RingWcid = 1002672, Radius = 30f }, ring: true);
            centre.Event.Stage(T0);
            Assert.IsFalse(centre.Event.RingHoldsAdjacents, "a 30 m circle in the middle of the block stays inside it");

            var off = Build(Outdoor(5f, 96f), arena, ring: false);
            off.Event.Stage(T0);
            Assert.IsFalse(off.Event.RingHoldsAdjacents, "the ring switch off holds nothing extra");

            var noWisp = Build(Outdoor(5f, 96f), new ArenaDef(), ring: true);
            noWisp.Event.Stage(T0);
            Assert.IsFalse(noWisp.Event.RingHoldsAdjacents, "ringWcid 0 means no ring");

            var indoor = Build(Indoor(), arena, ring: true);
            indoor.Event.Stage(T0);
            Assert.IsFalse(indoor.Event.RingHoldsAdjacents, "an indoor anchor gets no ring");
        }
    }
}
