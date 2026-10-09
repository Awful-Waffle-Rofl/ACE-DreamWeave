using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The Attack/Defend tick handler against a fake context (Docs/Pvp/ATTACK-DEFEND.md "Alerts"): destruction counted once per index
    /// in ScoreBoard[0], health-step alerts to the defenders once per step and throttled, the fall announced to everyone, and the status line.
    /// </summary>
    [TestClass]
    public class AttackDefendTickHandlerTests
    {
        private static readonly DateTime Start = FakeObjectiveContext.Start;
        private const int Max = 20000;

        // These tests pin the any-order alert, status and role machinery, so they run with pvp_bg_ad_sequential_crystals OFF; the
        // sequential behaviour (the default) is pinned by AttackDefendSequentialTests.
        private static BattlegroundDials Dials => BattlegroundTunables.Defaults with { AdSequentialCrystals = false };

        private static AttackDefendPlan Plan() => AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, Dials, attackerCount: 4);

        private static (AttackDefendTickHandler Handler, FakeObjectiveContext Ctx) Live(BattlegroundDials dials = null)
        {
            var handler = new AttackDefendTickHandler(dials ?? Dials, Plan());
            var ctx = new FakeObjectiveContext();

            handler.OnLive(ctx);

            return (handler, ctx);
        }

        private static void Full(FakeObjectiveContext ctx)
        {
            for (var i = 0; i < 3; i++)
                ctx.SetCrystal(i, Max, Max);
        }

        // ---------------- destruction ----------------

        [TestMethod]
        public void Destroyed_CountsOnceInScoreBoardZero_AndTellsEveryone()
        {
            var (h, ctx) = Live();

            Assert.IsTrue(h.OnCrystalDestroyed(ctx, 0));

            Assert.AreEqual(1, ctx.ScoreBoard[0]);
            CollectionAssert.AreEqual(new[] { "[Battleground] The Great Hall crystal (upper level, south-east of the octagon) has been destroyed! 2 of 3 remain." }, ctx.Announcements);
            Assert.AreEqual(0, ctx.TeamAnnouncements.Count, "the fall goes to everyone, not to one team");
            Assert.IsFalse(ctx.ScoreBoard.ContainsKey(1), "the defenders' score slot is never touched");
        }

        [TestMethod]
        public void DuplicateDestroyedIndex_IsIgnored()
        {
            var (h, ctx) = Live();

            Assert.IsTrue(h.OnCrystalDestroyed(ctx, 1));
            Assert.IsFalse(h.OnCrystalDestroyed(ctx, 1), "the same index reported twice");
            Assert.IsFalse(h.OnCrystalDestroyed(ctx, 1));

            Assert.AreEqual(1, ctx.ScoreBoard[0]);
            Assert.AreEqual(1, ctx.Announcements.Count);
        }

        [TestMethod]
        public void EachDistinctIndex_CountsAndTheRemainingCountFalls()
        {
            var (h, ctx) = Live();

            h.OnCrystalDestroyed(ctx, 2);
            h.OnCrystalDestroyed(ctx, 0);
            h.OnCrystalDestroyed(ctx, 1);

            Assert.AreEqual(3, ctx.ScoreBoard[0]);
            Assert.AreEqual("[Battleground] The Pit Hall crystal (bottom of the spiral stair, south-east of the West Cavern) has been destroyed! 2 of 3 remain.", ctx.Announcements[0]);
            Assert.AreEqual("[Battleground] The Great Hall crystal (upper level, south-east of the octagon) has been destroyed! 1 of 3 remain.", ctx.Announcements[1]);
            Assert.AreEqual("[Battleground] The West Cavern crystal (lower level, south from the octagon past the Defender room, then down the stairs) has been destroyed! 0 of 3 remain.", ctx.Announcements[2]);
        }

        [TestMethod]
        public void OutOfRangeIndex_IsIgnored()
        {
            var (h, ctx) = Live();

            Assert.IsFalse(h.OnCrystalDestroyed(ctx, -1));
            Assert.IsFalse(h.OnCrystalDestroyed(ctx, 3));
            Assert.IsFalse(h.OnCrystalDestroyed(ctx, 99));
            Assert.AreEqual(0, ctx.ScoreBoard.Count);
            Assert.AreEqual(0, ctx.Announcements.Count);
        }

        [TestMethod]
        public void ScoreBoardIsTheOnlyCount_ItAddsToWhatIsThere()
        {
            var (h, ctx) = Live();
            ctx.ScoreBoard[0] = 1;

            h.OnCrystalDestroyed(ctx, 0);

            Assert.AreEqual(2, ctx.ScoreBoard[0]);
        }

        // ---------------- health-step alerts ----------------

        [TestMethod]
        public void AStepCrossed_AlertsTheDefendersOnly_NamingSiteCompassAndPercent()
        {
            var (h, ctx) = Live();
            Full(ctx);
            ctx.SetCrystal(0, 15000, Max);

            h.OnTick(ctx, Start.AddSeconds(1));

            Assert.AreEqual(1, ctx.TeamAnnouncements.Count);
            Assert.AreEqual((1, "[Battleground] The Great Hall crystal (upper level, south-east of the octagon) is under attack - 75% remaining."), ctx.TeamAnnouncements[0]);
            Assert.AreEqual(0, ctx.Announcements.Count, "an alert is never sent to the attackers");
        }

        [TestMethod]
        public void BelowTheFirstStep_NoAlert()
        {
            var (h, ctx) = Live();
            Full(ctx);
            ctx.SetCrystal(0, 15001, Max);

            h.OnTick(ctx, Start.AddSeconds(1));

            Assert.AreEqual(0, ctx.TeamAnnouncements.Count, "24.99 percent lost is still step 0");
        }

        [TestMethod]
        public void EachStepAlertsOnce_AndAStaleSampleDoesNotRepeat()
        {
            var (h, ctx) = Live();
            Full(ctx);

            ctx.SetCrystal(0, 15000, Max);
            h.OnTick(ctx, Start.AddSeconds(1));
            h.OnTick(ctx, Start.AddSeconds(30));
            h.OnTick(ctx, Start.AddSeconds(60));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count, "step 1 alerted once however many ticks see it");

            ctx.SetCrystal(0, 12000, Max);
            h.OnTick(ctx, Start.AddSeconds(90));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count, "40 percent lost is still step 1");

            ctx.SetCrystal(0, 10000, Max);
            h.OnTick(ctx, Start.AddSeconds(120));
            Assert.AreEqual(2, ctx.TeamAnnouncements.Count);
            StringAssert.EndsWith(ctx.TeamAnnouncements[1].Text, "is under attack - 50% remaining.");

            ctx.SetCrystal(0, 4000, Max);
            h.OnTick(ctx, Start.AddSeconds(150));
            Assert.AreEqual(3, ctx.TeamAnnouncements.Count);
            StringAssert.EndsWith(ctx.TeamAnnouncements[2].Text, "is under attack - 20% remaining.");

            h.OnTick(ctx, Start.AddSeconds(180));
            Assert.AreEqual(3, ctx.TeamAnnouncements.Count, "no repeat at step 3");
        }

        /// <summary>
        /// Kill heals (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal") can lift a crystal back above an announced step. The latch follows
        /// it down, even on a tick inside the throttle window, so losing that step again alerts again. Reverting the latch-lowering line in
        /// AlertOnDamage leaves the second loss silent and fails the last assert.
        /// </summary>
        [TestMethod]
        public void AHealBackAboveAStep_ThenLosingItAgain_AlertsAgain()
        {
            var (h, ctx) = Live();
            Full(ctx);
            var interval = Dials.AdAlertMinIntervalSeconds;

            ctx.SetCrystal(0, 15000, Max);
            h.OnTick(ctx, Start.AddSeconds(1));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count, "fixture: step 1 announced");

            // Healed back above 75% on a tick still inside the throttle window of that alert.
            ctx.SetCrystal(0, 15100, Max);
            h.OnTick(ctx, Start.AddSeconds(2));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count, "a heal itself is silent");

            ctx.SetCrystal(0, 15000, Max);
            h.OnTick(ctx, Start.AddSeconds(2 + interval));
            Assert.AreEqual(2, ctx.TeamAnnouncements.Count, "the step lost again is announced again");
            StringAssert.EndsWith(ctx.TeamAnnouncements[1].Text, "is under attack - 75% remaining.");
        }

        [TestMethod]
        public void ADeepDropInOneTick_IsOneAlertAtTheCurrentPercent_NotOnePerStep()
        {
            var (h, ctx) = Live();
            Full(ctx);
            ctx.SetCrystal(0, 4000, Max);

            h.OnTick(ctx, Start.AddSeconds(1));

            Assert.AreEqual(1, ctx.TeamAnnouncements.Count);
            StringAssert.EndsWith(ctx.TeamAnnouncements[0].Text, "is under attack - 20% remaining.");

            // The skipped steps are not announced later: the crystal is at step 3 and has been told.
            h.OnTick(ctx, Start.AddSeconds(60));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count);
        }

        [TestMethod]
        public void TheAlertsAreThrottled_ByTheMinimumInterval_AndTheDeferredStepFiresLater()
        {
            var (h, ctx) = Live();
            Full(ctx);
            var interval = Dials.AdAlertMinIntervalSeconds;

            ctx.SetCrystal(0, 15000, Max);
            h.OnTick(ctx, Start.AddSeconds(1));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count);

            // A second crystal crosses a step inside the interval: held back, not lost.
            ctx.SetCrystal(1, 14000, Max);
            h.OnTick(ctx, Start.AddSeconds(1 + interval - 1));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count, "inside the minimum interval");

            h.OnTick(ctx, Start.AddSeconds(1 + interval));
            Assert.AreEqual(2, ctx.TeamAnnouncements.Count, "the deferred alert goes out once the interval has passed");
            StringAssert.Contains(ctx.TeamAnnouncements[1].Text, "West Cavern");
            StringAssert.EndsWith(ctx.TeamAnnouncements[1].Text, "is under attack - 70% remaining.");
        }

        [TestMethod]
        public void TwoCrystalsCrossingTogether_SendOneAlertPerInterval_LowestIndexFirst()
        {
            var (h, ctx) = Live();
            Full(ctx);
            ctx.SetCrystal(1, 14000, Max);
            ctx.SetCrystal(2, 10000, Max);

            h.OnTick(ctx, Start.AddSeconds(1));

            Assert.AreEqual(1, ctx.TeamAnnouncements.Count);
            StringAssert.Contains(ctx.TeamAnnouncements[0].Text, "West Cavern");

            h.OnTick(ctx, Start.AddSeconds(1 + Dials.AdAlertMinIntervalSeconds));

            Assert.AreEqual(2, ctx.TeamAnnouncements.Count);
            StringAssert.Contains(ctx.TeamAnnouncements[1].Text, "Pit Hall");
        }

        [TestMethod]
        public void ADestroyedCrystal_GetsNoUnderAttackAlert()
        {
            var (h, ctx) = Live();
            Full(ctx);
            ctx.SetCrystal(0, 5000, Max);

            h.OnCrystalDestroyed(ctx, 0);
            h.OnTick(ctx, Start.AddSeconds(1));

            Assert.AreEqual(0, ctx.TeamAnnouncements.Count, "a late sample of a crystal that already fell says nothing");
        }

        [TestMethod]
        public void TheStepSizeIsTheDialsSnapshot()
        {
            var (h, ctx) = Live(Dials with { AdAlertStepPercent = 50 });
            Full(ctx);
            ctx.SetCrystal(0, 15000, Max);

            h.OnTick(ctx, Start.AddSeconds(1));
            Assert.AreEqual(0, ctx.TeamAnnouncements.Count, "25 percent lost is below a 50 percent step");

            ctx.SetCrystal(0, 10000, Max);
            h.OnTick(ctx, Start.AddSeconds(2));
            Assert.AreEqual(1, ctx.TeamAnnouncements.Count);
        }

        // ---------------- status line ----------------

        [TestMethod]
        public void TheStatusLine_GoesToEveryone_EveryAnnounceInterval()
        {
            var (h, ctx) = Live();
            Full(ctx);

            h.OnTick(ctx, Start.AddSeconds(59));
            Assert.AreEqual(0, ctx.Announcements.Count);

            h.OnTick(ctx, Start.AddSeconds(60));
            CollectionAssert.AreEqual(new[] { "[Battleground] Crystals destroyed: 0 of 3. 09:00 remaining. Standing: Great Hall, West Cavern, Pit Hall." }, ctx.Announcements);

            h.OnCrystalDestroyed(ctx, 0);
            h.OnTick(ctx, Start.AddSeconds(120));

            Assert.AreEqual("[Battleground] Crystals destroyed: 1 of 3. 08:00 remaining. Standing: West Cavern, Pit Hall.", ctx.Announcements.Last());
            Assert.AreEqual(3, ctx.Announcements.Count, "status, the fall, status");
        }

        [TestMethod]
        public void TheStatusLine_ReadsTheCountFromTheScoreBoard()
        {
            var (h, ctx) = Live();
            ctx.ScoreBoard[0] = 2;

            h.OnTick(ctx, Start.AddSeconds(60));

            StringAssert.Contains(ctx.Announcements.Single(), "Crystals destroyed: 2 of 3.");
        }

        [TestMethod]
        public void AStallResumesWithOneStatusLine_NotABurst()
        {
            var (h, ctx) = Live();

            h.OnTick(ctx, Start.AddSeconds(500));
            h.OnTick(ctx, Start.AddSeconds(501));

            Assert.AreEqual(1, ctx.Announcements.Count);
        }

        [TestMethod]
        public void ATimeLeftPastTheLimit_ShowsZero()
        {
            var (h, ctx) = Live(Dials with { TimeLimitSecondsAd = 60, AdStatusAnnounceSeconds = 5 });

            h.OnTick(ctx, Start.AddSeconds(70));

            StringAssert.Contains(ctx.Announcements.Single(), "00:00 remaining.");
        }

        [TestMethod]
        public void NoPlan_OrAForeignContext_DoesNothing()
        {
            var noPlan = new AttackDefendTickHandler(Dials);
            var ctx = new FakeObjectiveContext();

            noPlan.OnLive(ctx);
            noPlan.OnTick(ctx, Start.AddSeconds(120));
            Assert.AreEqual(0, ctx.Announcements.Count);

            var (h, _) = Live();
            h.OnTick(new FakeBattlegroundContext(), Start.AddSeconds(120));
        }

        [TestMethod]
        public void BindPlan_ReplacesThePlaceholder_UntilACrystalHasFallen()
        {
            var (h, ctx) = Live();
            var two = AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, Dials with { AdCrystalCount = 2 }, 4);

            h.BindPlan(two);
            Assert.AreEqual(2, h.Plan.Count);

            h.OnCrystalDestroyed(ctx, 0);
            h.BindPlan(AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, Dials, 4));
            Assert.AreEqual(2, h.Plan.Count, "a plan bound after a crystal fell is ignored");

            h.BindPlan(null);
            Assert.AreEqual(2, h.Plan.Count);
        }

        // ---------------- IObjectiveModeHandler ----------------

        [TestMethod]
        public void TeamNames_AreAttackersAndDefenders()
        {
            var (h, _) = Live();

            Assert.AreEqual("Attackers", h.TeamName(0));
            Assert.AreEqual("Defenders", h.TeamName(1));
            Assert.AreEqual("Team 2", h.TeamName(2));
        }

        [TestMethod]
        public void ResultLine_AttackersWinAtScore_DefendersWinAtTimeout_NothingElse()
        {
            var (h, _) = Live();
            var attackers = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, false, true, EndReason.Score);
            var defenders = new MatchOutcome(new HashSet<int> { 1 }, new[] { 2, 1 }, false, true, EndReason.Timeout);
            var elimination = new MatchOutcome(new HashSet<int> { 1 }, new[] { 2, 1 }, false, true, EndReason.Elimination);
            var draw = new MatchOutcome(new HashSet<int>(), new[] { 1, 1 }, true, true, EndReason.Timeout);

            Assert.AreEqual("[Battleground] The Attackers have destroyed every crystal!", h.ResultLine(attackers, new Dictionary<int, int>()));
            Assert.AreEqual("[Battleground] The Defenders held every crystal until time ran out!", h.ResultLine(defenders, new Dictionary<int, int>()));
            Assert.IsNull(h.ResultLine(elimination, null));
            Assert.IsNull(h.ResultLine(draw, null));
            Assert.IsNull(h.ResultLine(null, null));
        }

        /// <summary>The defenders' timeout line once any crystal fell names how many they held; with none fallen it is the every-crystal line.</summary>
        [TestMethod]
        public void ResultLine_DefendersTimeout_NamesTheCrystalsHeld_OnceAnyFell()
        {
            var (h, _) = Live();
            var total = h.Plan.Count;
            var defenders = new MatchOutcome(new HashSet<int> { 1 }, new[] { 2, 1 }, false, true, EndReason.Timeout);

            Assert.AreEqual($"[Battleground] Time is up! The Defenders held {total - 1} of {total} crystals.", h.ResultLine(defenders, new Dictionary<int, int> { [0] = 1, [1] = 0 }));
            Assert.AreEqual("[Battleground] The Defenders held every crystal until time ran out!", h.ResultLine(defenders, new Dictionary<int, int> { [0] = 0 }));
            Assert.AreEqual("[Battleground] The Defenders held every crystal until time ran out!", h.ResultLine(defenders, null));
            Assert.AreEqual("Time is up! The Defenders held 2 of 3 crystals.", BattlegroundText.DefendersHeldBody(2, 3));
        }

        /// <summary>The role lines: the attacker's names the planned crystal count; any other team, and a handler with no plan, has none.</summary>
        [TestMethod]
        public void RoleLine_AttackerNamesTheCount_DefenderFixed_NothingElse()
        {
            var (h, _) = Live();

            Assert.AreEqual($"[Battleground] You are an Attacker. Destroy all {h.Plan.Count} Warding Crystals before time runs out! Crystals: Great Hall (upper level, south-east of the octagon), West Cavern (lower level, south from the octagon past the Defender room, then down the stairs), Pit Hall (bottom of the spiral stair, south-east of the West Cavern).", h.RoleLine(0));
            Assert.AreEqual("[Battleground] You are a Defender. Protect the Warding Crystals until time runs out! Crystals: Great Hall (upper level, south-east of the octagon), West Cavern (lower level, south from the octagon past the Defender room, then down the stairs), Pit Hall (bottom of the spiral stair, south-east of the West Cavern).", h.RoleLine(1));
            Assert.IsNull(h.RoleLine(2));
            Assert.IsNull(new AttackDefendTickHandler(BattlegroundTunables.Defaults).RoleLine(0), "no plan bound: no line");
            Assert.AreEqual("You are an Attacker. Destroy all 3 Warding Crystals before time runs out!", BattlegroundText.RoleAttackerBody(3));
        }

        /// <summary>
        /// The formation seam both handlers implement: Attack/Defend binds the plan and has no ring; King of the Hill binds the layout,
        /// rings the zone it scores, and counts no destroyed objective.
        /// </summary>
        [TestMethod]
        public void BindMatch_MarkerZone_AndObjectiveDestroyed_PerMode()
        {
            var d = BattlegroundTunables.Defaults;
            var plan = AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, d, 3);

            IObjectiveModeHandler ad = new AttackDefendTickHandler(d);
            ad.BindMatch(BattlegroundMapCatalog.Bg003c, plan);
            Assert.AreSame(plan, ((AttackDefendTickHandler)ad).Plan);
            Assert.IsNull(ad.MarkerZone);

            var koth = new KothTickHandler(d, BattlegroundModes.KothZoneFor(BattlegroundMapCatalog.Bg016c, d));
            IObjectiveModeHandler asHandler = koth;
            asHandler.BindMatch(BattlegroundMapCatalog.Bg016c, null);
            Assert.AreEqual(BattlegroundModes.KothZoneFor(BattlegroundMapCatalog.Bg016c, d), asHandler.MarkerZone);
            Assert.AreEqual(koth.Zone, asHandler.MarkerZone);
            Assert.IsFalse(asHandler.OnObjectiveDestroyed(new FakeObjectiveContext(), 0));
        }

        /// <summary>Review N4: one crystal reads "the Warding Crystal"; two or more keep "all N Warding Crystals". Pinned on the body and through a one-crystal plan.</summary>
        [TestMethod]
        public void RoleLine_OneCrystal_IsSingular_TwoOrMoreKeepTheCount()
        {
            Assert.AreEqual("You are an Attacker. Destroy the Warding Crystal before time runs out!", BattlegroundText.RoleAttackerBody(1));
            Assert.AreEqual("You are an Attacker. Destroy all 2 Warding Crystals before time runs out!", BattlegroundText.RoleAttackerBody(2));
            Assert.AreEqual("You are an Attacker. Destroy all 3 Warding Crystals before time runs out!", BattlegroundText.RoleAttackerBody(3));

            var d = BattlegroundTunables.Defaults with { AdCrystalCount = 1 };
            var one = new AttackDefendTickHandler(d, AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, d, 2));
            Assert.AreEqual(1, one.Plan.Count, "fixture: a one-crystal plan");
            Assert.AreEqual("[Battleground] You are an Attacker. Destroy the Warding Crystal before time runs out! Crystals: Great Hall (upper level, south-east of the octagon).", one.RoleLine(0));
        }

        [TestMethod]
        public void ExactBodies_AreThePlayerFacingStrings()
        {
            Assert.AreEqual("The Attackers have destroyed every crystal!", BattlegroundText.AttackersWinBody);
            Assert.AreEqual("The Defenders held every crystal until time ran out!", BattlegroundText.DefendersWinBody);
            Assert.AreEqual("The Great Hall crystal (north-east) is under attack - 50% remaining.", BattlegroundText.CrystalUnderAttackBody("Great Hall", "north-east", 50));
            Assert.AreEqual("The West Cavern crystal (north-west) has been destroyed! 1 of 3 remain.", BattlegroundText.CrystalDestroyedBody("West Cavern", "north-west", 1, 3));
            Assert.AreEqual("Crystals destroyed: 1 of 3. 09:05 remaining.", BattlegroundText.CrystalStatusBody(1, 3, 545));
            Assert.AreEqual("Crystals destroyed: 0 of 4. 00:00 remaining.", BattlegroundText.CrystalStatusBody(0, 4, -3));
            Assert.AreEqual("Attack/Defend", BattlegroundText.ModeLabelAttackDefend);
            Assert.AreEqual("[Battleground] ", BattlegroundText.Prefix);
        }

        [TestMethod]
        public void StatusLine_IsTheArenaStatusSuffix()
        {
            var (h, _) = Live();

            Assert.AreEqual(" Crystals destroyed: 2 of 3.", h.StatusLine(new Dictionary<int, int> { [0] = 2 }, 3));
            Assert.AreEqual(" Crystals destroyed: 0 of 3.", h.StatusLine(new Dictionary<int, int>(), 3));
            Assert.AreEqual("", h.StatusLine(null, 3));
            Assert.AreEqual("", h.StatusLine(new Dictionary<int, int>(), 0));
        }

        // ---------------- King of the Hill keeps its strings, byte for byte ----------------

        [TestMethod]
        public void Koth_ImplementsTheObjectiveSeam_WithItsExistingStrings()
        {
            var koth = new KothTickHandler(Dials, new KothZone(0, 0, 0, 6, 3)) as IObjectiveModeHandler;

            Assert.IsNotNull(koth);
            Assert.AreEqual("West", koth.TeamName(0));
            Assert.AreEqual("East", koth.TeamName(1));
            Assert.AreEqual("Team 4", koth.TeamName(4));

            var west = new HashSet<int> { 0 };
            var east = new HashSet<int> { 1 };
            var scores = new Dictionary<int, int> { [0] = 300, [1] = 120 };

            Assert.AreEqual("[Battleground] West has taken the hill! Final score: West 300, East 120.",
                koth.ResultLine(new MatchOutcome(west, new[] { 1, 2 }, false, true, EndReason.Score), scores));
            Assert.AreEqual("[Battleground] Time is up. East wins, West 300, East 120.",
                koth.ResultLine(new MatchOutcome(east, new[] { 2, 1 }, false, true, EndReason.Timeout), scores));
            Assert.AreEqual("[Battleground] Time is up. The match is a draw, West 5, East 5.",
                koth.ResultLine(new MatchOutcome(new HashSet<int>(), new[] { 1, 1 }, true, true, EndReason.Timeout), new Dictionary<int, int> { [0] = 5, [1] = 5 }));
            Assert.IsNull(koth.ResultLine(new MatchOutcome(east, new[] { 2, 1 }, false, true, EndReason.Elimination), scores));

            Assert.AreEqual(" Score: West 300, East 120, first to 300 wins.", koth.StatusLine(scores, 300));
            Assert.AreEqual("", koth.StatusLine(scores, 0), "no target: no suffix, as the status command always did");
            Assert.AreEqual("", koth.StatusLine(null, 300));
        }


        // ---------------- hints (review F4, F5, F6) ----------------

        private const string HintGreatHall = "upper level, south-east of the octagon";
        private const string HintWestCavern = "lower level, south from the octagon past the Defender room, then down the stairs";
        private const string HintPitHall = "bottom of the spiral stair, south-east of the West Cavern";

        private static AttackDefendTickHandler HandlerForCount(int count)
        {
            var d = BattlegroundTunables.Defaults with { AdCrystalCount = count, AdSequentialCrystals = false };

            return new AttackDefendTickHandler(d, AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, d, 4));
        }

        /// <summary>F4: both role lines end with the crystals in play, and ONLY those: count 1 lists one site, 2 lists two, 3 lists all.</summary>
        [TestMethod]
        public void RoleLines_ListOnlyTheInPlaySites_ForCounts1To3()
        {
            var great = $"Great Hall ({HintGreatHall})";
            var west = $"West Cavern ({HintWestCavern})";
            var pit = $"Pit Hall ({HintPitHall})";

            var expected = new Dictionary<int, string>
            {
                [1] = $"Crystals: {great}.",
                [2] = $"Crystals: {great}, {west}.",
                [3] = $"Crystals: {great}, {west}, {pit}.",
            };

            foreach (var (count, list) in expected)
            {
                var h = HandlerForCount(count);

                Assert.AreEqual(count, h.Plan.Count, "fixture: the plan has the asked count");
                StringAssert.EndsWith(h.RoleLine(0), " " + list, $"attacker line, count {count}");
                StringAssert.EndsWith(h.RoleLine(1), " " + list, $"defender line, count {count}");
            }

            Assert.IsFalse(HandlerForCount(1).RoleLine(0).Contains("West Cavern"), "count 1 must not list a site that is not in play");
            Assert.IsFalse(HandlerForCount(2).RoleLine(1).Contains("Pit Hall"), "count 2 must not list the third site");
        }

        [TestMethod]
        public void CrystalList_NamesASiteWithoutAHintAlone_AndIsEmptyForNone()
        {
            var plain = new BattlegroundCrystalSite("Cellar", 0x0101, 0f, 0f, 0f);
            var hinted = new BattlegroundCrystalSite("Attic", 0x0102, 0f, 0f, 0f, "top floor");

            Assert.AreEqual("Crystals: Cellar, Attic (top floor).", BattlegroundText.CrystalList(new[] { plain, hinted }));
            Assert.AreEqual("", BattlegroundText.CrystalList(Array.Empty<BattlegroundCrystalSite>()));
            Assert.AreEqual("", BattlegroundText.CrystalList(null));
        }

        /// <summary>F6: the defender line mirrors the attacker's singular branch for one crystal, and stays plural otherwise.</summary>
        [TestMethod]
        public void DefenderRoleLine_OneCrystal_IsSingular()
        {
            Assert.AreEqual("You are a Defender. Protect the Warding Crystal until time runs out!", BattlegroundText.RoleDefenderBody(1));
            Assert.AreEqual("You are a Defender. Protect the Warding Crystals until time runs out!", BattlegroundText.RoleDefenderBody(2));
            Assert.AreEqual("You are a Defender. Protect the Warding Crystals until time runs out!", BattlegroundText.RoleDefenderBody(3));

            StringAssert.StartsWith(HandlerForCount(1).RoleLine(1), "[Battleground] You are a Defender. Protect the Warding Crystal until time runs out! Crystals:");
            StringAssert.StartsWith(HandlerForCount(2).RoleLine(1), "[Battleground] You are a Defender. Protect the Warding Crystals until time runs out! Crystals:");
        }

        /// <summary>F5: with a hint the alerts quote the hint and NOT the compass word.</summary>
        [TestMethod]
        public void Alerts_UseTheHint_NotTheCompassWord()
        {
            var (h, ctx) = Live();
            Full(ctx);
            ctx.SetCrystal(1, 15000, Max);

            h.OnTick(ctx, Start.AddSeconds(1));
            h.OnCrystalDestroyed(ctx, 2);

            Assert.AreEqual($"[Battleground] The West Cavern crystal ({HintWestCavern}) is under attack - 75% remaining.", ctx.TeamAnnouncements.Single().Text);
            Assert.AreEqual($"[Battleground] The Pit Hall crystal ({HintPitHall}) has been destroyed! 2 of 3 remain.", ctx.Announcements.Single());
            Assert.IsFalse(ctx.TeamAnnouncements.Single().Text.Contains("north-west"));
            Assert.IsFalse(ctx.Announcements.Single().Contains("south)"));
        }

        /// <summary>F5: a site with no hint keeps the compass word, in both alerts.</summary>
        [TestMethod]
        public void Alerts_ForASiteWithNoHint_KeepTheCompassWord()
        {
            var plain = new BattlegroundCrystalSite("Cellar", 0x0101, 0f, 0f, 0f);
            var plan = new AttackDefendPlan(new[] { new PlannedCrystal(0, plain, Max, "north") }, Max, 1);
            var h = new AttackDefendTickHandler(Dials, plan);
            var ctx = new FakeObjectiveContext();

            h.OnLive(ctx);
            ctx.SetCrystal(0, 15000, Max);
            h.OnTick(ctx, Start.AddSeconds(1));
            h.OnCrystalDestroyed(ctx, 0);

            Assert.AreEqual("[Battleground] The Cellar crystal (north) is under attack - 75% remaining.", ctx.TeamAnnouncements.Single().Text);
            Assert.AreEqual("[Battleground] The Cellar crystal (north) has been destroyed! 0 of 1 remain.", ctx.Announcements.Single());
            Assert.AreEqual("north", plan.Crystals[0].Where);
        }

        /// <summary>F4: the status announcement names the crystals still standing, and drops a destroyed one.</summary>
        [TestMethod]
        public void TheStatusLine_NamesTheStandingCrystals()
        {
            var (h, ctx) = Live();
            Full(ctx);

            h.OnTick(ctx, Start.AddSeconds(60));
            Assert.AreEqual("[Battleground] Crystals destroyed: 0 of 3. 09:00 remaining. Standing: Great Hall, West Cavern, Pit Hall.", ctx.Announcements.Single());

            h.OnCrystalDestroyed(ctx, 1);
            h.OnTick(ctx, Start.AddSeconds(120));
            Assert.AreEqual("[Battleground] Crystals destroyed: 1 of 3. 08:00 remaining. Standing: Great Hall, Pit Hall.", ctx.Announcements.Last());

            Assert.AreEqual("Crystals destroyed: 3 of 3. 01:00 remaining.", BattlegroundText.CrystalStatusBody(3, 3, 60, Array.Empty<string>()), "nothing standing: no suffix");
        }

        [TestMethod]
        public void Koth_TextMethodsAreUnchanged()
        {
            Assert.AreEqual("[Battleground] West holds the hill!", BattlegroundText.ZoneHeld(0));
            Assert.AreEqual("[Battleground] The hill is contested!", BattlegroundText.ZoneContested());
            Assert.AreEqual("[Battleground] The hill stands empty.", BattlegroundText.ZoneNeutral());
            Assert.AreEqual("[Battleground] The hill has moved! Head to the north.", BattlegroundText.HillMoved("north"));
            Assert.AreEqual("[Battleground] Score: West 1, East 2.", BattlegroundText.Scores(1, 2));
            Assert.AreEqual("West Team", BattlegroundText.TeamFellowshipName(0));
            Assert.AreEqual("King of the Hill", BattlegroundText.ModeLabelKoth);
            Assert.AreEqual("West", BattlegroundText.TeamName(0));
            Assert.AreEqual("East", BattlegroundText.TeamName(1));
        }
    }
}
