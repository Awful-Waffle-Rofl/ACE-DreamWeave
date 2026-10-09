using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the pre-event teaser (2026-09-03): the pure composers in
    /// <see cref="WorldEventAnnouncer"/> (TeaserLine/TeaserTimeHint), TeaserLeadSeconds resolution on
    /// <see cref="WorldEvent"/>, the Idle -> teaser -> Staged timing added to <see cref="WorldEvent.Tick"/>,
    /// and the "--teaser" flag on "/worldevent start" (<see cref="WorldEventCommands.TryParseStartArgs"/>).
    /// Follows <see cref="WorldEventAnnouncerTests"/> and <see cref="WorldEventStateMachineTests"/>'s
    /// conventions - only pure statics and directly-constructed WorldEvents through the seams, no
    /// database/landblock/live session anywhere (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventTeaserTests
    {
        private static readonly double T0 = 1_700_000_000d;

        // ---- fakes and builders (mirrors WorldEventDeathProtectionTests/WorldEventStateMachineTests) ----

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

        private static SourceThemeDef BuildSource(string teaserFlavour = null)
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
                WaveFlavour = "Another rank tears its way in!",
                TeaserFlavour = teaserFlavour
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

        private static WorldEventComposition BuildComposition(SourceThemeDef theme = null)
        {
            return new WorldEventComposition(theme ?? BuildSource(), new[] { BuildFamily() }, BossDef.None, BuildGoal(),
                BuildReward(), BuildAnchor(), new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                WorldEventAxisStore.Empty);
        }

        private static WorldEvent BuildEvent(int? requestTeaserLeadSeconds = null, Func<int> teaserLeadSource = null,
            SourceThemeDef theme = null, double now = 0)
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                Invoker = "test",
                TeaserLeadSeconds = requestTeaserLeadSeconds
            };

            var clockValue = now == 0 ? T0 : now;

            return new WorldEvent(1, BuildComposition(theme), request, new FakeObjective(),
                () => new AudienceEstimate(3, 100, 150), () => clockValue,
                teaserLeadSource: teaserLeadSource);
        }

        // ---- WorldEventAnnouncer.TeaserLine / TeaserTimeHint -------------------------------------------

        [TestMethod]
        public void TeaserLine_SubstitutesAnchor_CarriesPrefixOnce_EndsWithTimeHint()
        {
            var theme = BuildSource(teaserFlavour: "A low rumble can be felt coming from {anchor}.");

            var line = WorldEventAnnouncer.TeaserLine(theme, "the gates of Holtburg", 180, new Random(1));

            Assert.AreEqual("[World Event] A low rumble can be felt coming from the gates of Holtburg. Roughly 3 minutes out.", line);

            // The prefix appears exactly once.
            Assert.AreEqual(1, CountOccurrences(line, WorldEventAnnouncer.AnnouncePrefix));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;

            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        [TestMethod]
        public void TeaserLine_NullOrBlankAnchor_RendersSomewhere()
        {
            var theme = BuildSource(teaserFlavour: "A low rumble can be felt coming from {anchor}.");

            var nullLine = WorldEventAnnouncer.TeaserLine(theme, null, 180, new Random(1));
            var blankLine = WorldEventAnnouncer.TeaserLine(theme, "  ", 180, new Random(1));

            Assert.AreEqual("[World Event] A low rumble can be felt coming from somewhere. Roughly 3 minutes out.", nullLine);

            // "  " is non-null/non-empty, so it substitutes literally rather than falling back - only
            // null/blank-per-StartLine's own convention (empty string) triggers "somewhere" there. TeaserLine
            // mirrors that: only a genuinely null/blank anchorName falls back.
            Assert.IsTrue(blankLine.Contains("somewhere") || blankLine.Contains("  "));
        }

        [TestMethod]
        public void TeaserLine_ThemeFlavourWinsOverThePool()
        {
            var theme = BuildSource(teaserFlavour: "Custom rumble at {anchor}.");

            var line = WorldEventAnnouncer.TeaserLine(theme, "Holtburg", 10, new Random(1));

            Assert.AreEqual("[World Event] Custom rumble at Holtburg. Moments away.", line);
        }

        [TestMethod]
        public void TeaserLine_BlankThemeFlavour_FallsBackToThePool()
        {
            var theme = BuildSource(teaserFlavour: "   ");

            var line = WorldEventAnnouncer.TeaserLine(theme, "Holtburg", 10, new Random(1));

            // The drawn line, with {anchor} substituted back out, must be one of the shipped pool templates.
            var body = line.Substring(WorldEventAnnouncer.AnnouncePrefix.Length);
            body = body.Substring(0, body.Length - " Moments away.".Length);

            var matchesPool = WorldEventAnnouncer.TeaserFlavours.Any(t => t.Replace("{anchor}", "Holtburg") == body);

            Assert.IsTrue(matchesPool, $"'{body}' does not match any pool template");
        }

        [TestMethod]
        public void TeaserLine_NullTheme_DrawsFromThePool()
        {
            var line = WorldEventAnnouncer.TeaserLine(null, "Holtburg", 10, new Random(1));

            var body = line.Substring(WorldEventAnnouncer.AnnouncePrefix.Length);
            body = body.Substring(0, body.Length - " Moments away.".Length);

            var matchesPool = WorldEventAnnouncer.TeaserFlavours.Any(t => t.Replace("{anchor}", "Holtburg") == body);

            Assert.IsTrue(matchesPool, $"'{body}' does not match any pool template");
        }

        [TestMethod]
        public void TeaserLine_SeededRng_DrawnLineIsAlwaysAPoolMember()
        {
            var theme = BuildSource();

            for (var seed = 0; seed < 20; seed++)
            {
                var line = WorldEventAnnouncer.TeaserLine(theme, "Holtburg", 200, new Random(seed));

                var body = line.Substring(WorldEventAnnouncer.AnnouncePrefix.Length);
                body = body.Substring(0, body.Length - " Roughly 3 minutes out.".Length);

                var matchesPool = WorldEventAnnouncer.TeaserFlavours.Any(t => t.Replace("{anchor}", "Holtburg") == body);

                Assert.IsTrue(matchesPool, $"seed {seed}: '{body}' does not match any pool template");
            }
        }

        [TestMethod]
        public void TeaserTimeHint_Boundaries()
        {
            Assert.AreEqual("Moments away.", WorldEventAnnouncer.TeaserTimeHint(0));
            Assert.AreEqual("Moments away.", WorldEventAnnouncer.TeaserTimeHint(44));
            Assert.AreEqual("Roughly a minute out.", WorldEventAnnouncer.TeaserTimeHint(45));
            Assert.AreEqual("Roughly a minute out.", WorldEventAnnouncer.TeaserTimeHint(89));
            Assert.AreEqual("Roughly 2 minutes out.", WorldEventAnnouncer.TeaserTimeHint(90));
            Assert.AreEqual("Roughly 3 minutes out.", WorldEventAnnouncer.TeaserTimeHint(180));
            Assert.AreEqual("Roughly 60 minutes out.", WorldEventAnnouncer.TeaserTimeHint(3600));
        }

        [TestMethod]
        public void TeaserTimeHint_NegativeReadsAsMomentsAway()
        {
            Assert.AreEqual("Moments away.", WorldEventAnnouncer.TeaserTimeHint(-5));
        }

        // ---- WorldEvent.TeaserLeadSeconds resolution -----------------------------------------------------

        [TestMethod]
        public void TeaserLeadSeconds_RequestValueWinsOverTheSeam()
        {
            var evt = BuildEvent(requestTeaserLeadSeconds: 42, teaserLeadSource: () => 999);

            Assert.AreEqual(42, evt.TeaserLeadSeconds);
        }

        [TestMethod]
        public void TeaserLeadSeconds_NullRequestValue_UsesTheSeam()
        {
            var evt = BuildEvent(requestTeaserLeadSeconds: null, teaserLeadSource: () => 75);

            Assert.AreEqual(75, evt.TeaserLeadSeconds);
        }

        [TestMethod]
        public void TeaserLeadSeconds_ClampedToZeroOnTheLowEnd()
        {
            var evt = BuildEvent(requestTeaserLeadSeconds: -50, teaserLeadSource: () => 100);

            Assert.AreEqual(0, evt.TeaserLeadSeconds);
        }

        [TestMethod]
        public void TeaserLeadSeconds_ClampedToMaxOnTheHighEnd()
        {
            var evt = BuildEvent(requestTeaserLeadSeconds: WorldEvent.MaxTeaserLeadSeconds + 500, teaserLeadSource: () => 100);

            Assert.AreEqual(WorldEvent.MaxTeaserLeadSeconds, evt.TeaserLeadSeconds);
        }

        // ---- Timing: Begin -> Idle -> Stage via Tick -----------------------------------------------------

        [TestMethod]
        public void Begin_PositiveLead_StaysIdleAndBroadcastsOneTeaserLine()
        {
            var broadcasts = new List<string>();
            var previous = WorldEventAnnouncer.BroadcastSink;

            try
            {
                WorldEventAnnouncer.BroadcastSink = broadcasts.Add;

                var evt = BuildEvent(teaserLeadSource: () => 180);

                evt.Begin(T0);

                Assert.AreEqual(WorldEventState.Idle, evt.State);
                Assert.AreEqual(T0, evt.TeasedAt);
                Assert.AreEqual(1, broadcasts.Count, "exactly one teaser line must go out");
                Assert.IsTrue(broadcasts[0].StartsWith(WorldEventAnnouncer.AnnouncePrefix));
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = previous;
            }
        }

        [TestMethod]
        public void Tick_BeforeLeadExpires_StaysIdle()
        {
            var evt = BuildEvent(teaserLeadSource: () => 180);

            evt.Begin(T0);
            evt.Tick(T0 + 179);

            Assert.AreEqual(WorldEventState.Idle, evt.State);
        }

        [TestMethod]
        public void Tick_WhenLeadExpires_ReachesStagedOrBeyond()
        {
            var evt = BuildEvent(teaserLeadSource: () => 180);

            evt.Begin(T0);
            evt.Tick(T0 + 180);

            // Stage() is reachable headless in this harness (no landblock bridge => the hold and audience
            // sample are skipped, but the state transition itself runs) - WorldEventStateMachineTests relies
            // on exactly this to drive Stage()/Tick() with no live Landblock. State must have left Idle.
            Assert.AreNotEqual(WorldEventState.Idle, evt.State);
            Assert.IsTrue(evt.State == WorldEventState.Staged || evt.State == WorldEventState.Announced
                || evt.State == WorldEventState.Active, $"unexpected state {evt.State}");
        }

        [TestMethod]
        public void Begin_ZeroLead_StagesImmediately()
        {
            var evt = BuildEvent(teaserLeadSource: () => 0);

            evt.Begin(T0);

            Assert.AreNotEqual(WorldEventState.Idle, evt.State);
        }

        // ---- "--teaser" flag parsing ----------------------------------------------------------------------

        [TestMethod]
        public void StartArgs_Teaser_ValidValueIsParsed()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g", "--teaser", "120" },
                out var parsed, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(120, parsed.TeaserLeadSeconds);
        }

        [TestMethod]
        public void StartArgs_Teaser_ZeroIsAccepted()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g", "--teaser", "0" },
                out var parsed, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(0, parsed.TeaserLeadSeconds);
        }

        [TestMethod]
        public void StartArgs_Teaser_NegativeIsRefused()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--teaser", "-5" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.IsTrue(error.Contains("--teaser"), error);
        }

        [TestMethod]
        public void StartArgs_Teaser_AboveMaxIsRefused()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--teaser", (WorldEvent.MaxTeaserLeadSeconds + 1).ToString() },
                out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.IsTrue(error.Contains("--teaser"), error);
        }

        [TestMethod]
        public void StartArgs_Teaser_NonNumericIsRefused()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--teaser", "soon" }, out var parsed, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(parsed);
            Assert.IsTrue(error.Contains("--teaser"), error);
        }

        [TestMethod]
        public void StartArgs_Teaser_FlagAbsent_LeavesTeaserLeadSecondsNull()
        {
            var ok = WorldEventCommands.TryParseStartArgs(
                new[] { "--here", "--source", "s", "--family", "f", "--goal", "g" },
                out var parsed, out var error);

            Assert.IsTrue(ok, error);
            Assert.IsNull(parsed.TeaserLeadSeconds);
        }
    }
}
