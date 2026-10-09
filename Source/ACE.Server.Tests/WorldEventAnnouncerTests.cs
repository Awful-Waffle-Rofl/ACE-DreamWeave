using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the pure line composers in <see cref="WorldEventAnnouncer"/> (TECH-DESIGN 2.6,
    /// PLAN 1.8/2.10). Only the composers are tested - Broadcast/Local/Audit are thin delivery wrappers
    /// around live PlayerManager/Player calls, which D6 forbids a test from standing up.
    /// </summary>
    [TestClass]
    public class WorldEventAnnouncerTests
    {
        private static SourceThemeDef BuildSource(string startFlavour = "Something is coming through the weave near {anchor}.",
            string waveFlavour = "Another rank tears its way in!")
        {
            return new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                StartFlavour = startFlavour,
                WaveFlavour = waveFlavour
            };
        }

        private static FamilyDef BuildFamily(string displayName = "the Emberwrought")
        {
            return new FamilyDef { Id = "emberwrought", DisplayName = displayName };
        }

        private static GoalDef BuildGoal(string displayName = "Kill Count")
        {
            return new GoalDef { Id = "kill_count", DisplayName = displayName };
        }

        private static ParticipantRecord Killer(string name, int kills)
        {
            return new ParticipantRecord { Name = name, Kills = kills };
        }

        // ---- StartLine --------------------------------------------------------------------------------

        [TestMethod]
        public void StartLine_ReplacesAnchorAndAppendsFamilyTag()
        {
            var line = WorldEventAnnouncer.StartLine(BuildSource(), BuildFamily().DisplayName, "the gates of Holtburg");

            Assert.AreEqual("[World Event] Something is coming through the weave near the gates of Holtburg. (the Emberwrought)", line);
        }

        [TestMethod]
        public void StartLine_NullTheme_YieldsOnlyTheFamilyTag()
        {
            var line = WorldEventAnnouncer.StartLine(null, BuildFamily().DisplayName, "somewhere");

            Assert.AreEqual("[World Event] (the Emberwrought)", line);
        }

        [TestMethod]
        public void StartLine_NullFamily_YieldsOnlyTheFlavour()
        {
            var line = WorldEventAnnouncer.StartLine(BuildSource(), null, "the gates of Holtburg");

            Assert.AreEqual("[World Event] Something is coming through the weave near the gates of Holtburg.", line);
        }

        [TestMethod]
        public void StartLine_TwoFamilies_JoinTheirDisplayNamesWithAnd()
        {
            // Two-family composition (2026-08-29). The announcer takes the already-joined string, which is
            // what WorldEventComposition.FamilyDisplayName produces - see the composition test below.
            var line = WorldEventAnnouncer.StartLine(BuildSource(), "the Blackwing and the Drudges", "the gates of Holtburg");

            Assert.AreEqual("[World Event] Something is coming through the weave near the gates of Holtburg. (the Blackwing and the Drudges)", line);
        }

        [TestMethod]
        public void FamilyDisplayName_JoinsEveryComposedFamilyWithAnd()
        {
            var composition = new WorldEventComposition(BuildSource(),
                new[]
                {
                    new FamilyDef { Id = "blackwing", DisplayName = "the Blackwing" },
                    new FamilyDef { Id = "drudge", DisplayName = "the Drudges" }
                },
                null, BuildGoal(), null, null, null, null);

            Assert.AreEqual("the Blackwing and the Drudges", composition.FamilyDisplayName);

            var single = new WorldEventComposition(BuildSource(), new[] { BuildFamily() },
                null, BuildGoal(), null, null, null, null);

            Assert.AreEqual("the Emberwrought", single.FamilyDisplayName);

            var none = new WorldEventComposition(BuildSource(), null, null, BuildGoal(), null, null, null, null);

            Assert.IsNull(none.FamilyDisplayName, "no composed family still drops the tag entirely");
        }

        // ---- ActiveLine --------------------------------------------------------------------------------

        [TestMethod]
        public void ActiveLine_MatchesTheFixedShape()
        {
            var line = WorldEventAnnouncer.ActiveLine(BuildSource(), BuildFamily().DisplayName, BuildGoal(), "the gates of Holtburg");

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg - the Emberwrought pour in!", line);
        }

        [TestMethod]
        public void ActiveLine_TwoFamilies_JoinTheirDisplayNamesWithAnd()
        {
            var line = WorldEventAnnouncer.ActiveLine(BuildSource(), "the Blackwing and the Drudges", BuildGoal(), "the gates of Holtburg");

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg - the Blackwing and the Drudges pour in!", line);
        }

        // ---- WaveLine ----------------------------------------------------------------------------------

        [TestMethod]
        public void WaveLine_ReturnsTheThemesWaveFlavour()
        {
            Assert.AreEqual("Another rank tears its way in!", WorldEventAnnouncer.WaveLine(BuildSource()));
        }

        [TestMethod]
        public void WaveLine_NullTheme_ReturnsEmpty()
        {
            Assert.AreEqual("", WorldEventAnnouncer.WaveLine(null));
        }

        // ---- WarnLine ----------------------------------------------------------------------------------

        [TestMethod]
        public void WarnLine_MatchesTheFixedShape()
        {
            Assert.AreEqual("[World Event] 60 seconds remain!", WorldEventAnnouncer.WarnLine(60));
            Assert.AreEqual("[World Event] 30 seconds remain!", WorldEventAnnouncer.WarnLine(30));
        }

        // ---- ProgressLine ------------------------------------------------------------------------------

        [TestMethod]
        public void ProgressLine_MatchesTheFixedShape()
        {
            var line = WorldEventAnnouncer.ProgressLine(BuildGoal(), null, "the gates of Holtburg",
                "12 of 40 slain.", 3, 90);

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg: 12 of 40 slain. Wave 3. 1m30s remain.", line);
        }

        [TestMethod]
        public void ProgressLine_OmitsRemainClauseWithoutDeadline()
        {
            var line = WorldEventAnnouncer.ProgressLine(BuildGoal(), null, "the gates of Holtburg",
                "Vhaleth still stands.", 1, -1);

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg: Vhaleth still stands. Wave 1.", line);
        }

        [TestMethod]
        public void ProgressLine_OmitsWaveClauseAtWaveZero()
        {
            var line = WorldEventAnnouncer.ProgressLine(BuildGoal(), null, "the gates of Holtburg",
                "Vhaleth still stands.", 0, 90);

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg: Vhaleth still stands. 1m30s remain.", line);
        }

        [TestMethod]
        public void ProgressLine_OmitsBothClauses_LeavesOnlyGoalAndProgress()
        {
            var line = WorldEventAnnouncer.ProgressLine(BuildGoal(), null, "the gates of Holtburg",
                "Vhaleth still stands.", 0, -1);

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg: Vhaleth still stands.", line);
        }

        [TestMethod]
        public void ProgressLine_UsesTheGoalDisplayNameOverrideWhenPresent()
        {
            var line = WorldEventAnnouncer.ProgressLine(BuildGoal(), "Shatter the Pillars", "the gates of Holtburg",
                "2 of 4 pillars shattered.", 0, -1);

            Assert.AreEqual("[World Event] Shatter the Pillars at the gates of Holtburg: 2 of 4 pillars shattered.", line);
        }

        // ---- BossArrivedLine ---------------------------------------------------------------------------

        [TestMethod]
        public void BossArrivedLine_MatchesTheFixedShape()
        {
            var line = WorldEventAnnouncer.BossArrivedLine("Vhaleth the Undying", "the gates of Holtburg");

            Assert.AreEqual("[World Event] Vhaleth the Undying has arrived at the gates of Holtburg!", line);
        }

        [TestMethod]
        public void BossArrivedLine_NullName_FallsBackToTheChampion()
        {
            var line = WorldEventAnnouncer.BossArrivedLine(null, "the gates of Holtburg");

            Assert.AreEqual("[World Event] the champion has arrived at the gates of Holtburg!", line);
        }

        // ---- BossHealthMilestoneLine --------------------------------------------------------------------

        [TestMethod]
        public void BossHealthMilestoneLine_MatchesTheFixedShape()
        {
            var line = WorldEventAnnouncer.BossHealthMilestoneLine("Vhaleth the Undying", 50);

            Assert.AreEqual("[World Event] Vhaleth the Undying is at 50% health!", line);
        }

        [TestMethod]
        public void BossHealthMilestoneLine_NullName_FallsBackToTheChampion()
        {
            var line = WorldEventAnnouncer.BossHealthMilestoneLine(null, 25);

            Assert.AreEqual("[World Event] the champion is at 25% health!", line);
        }

        // ---- OutcomeLine: Success ------------------------------------------------------------------------

        [TestMethod]
        public void OutcomeLine_Success_NoMvp_StillReportsKillsAndParticipants()
        {
            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, BuildGoal(), "the gates of Holtburg",
                WorldEventMvp.None, null, 0);

            Assert.AreEqual("[World Event] Kill Count at the gates of Holtburg succeeded! Most kills: none (0). 0 defenders took part.", line);
        }

        [TestMethod]
        public void OutcomeLine_Success_WithKillingBlowMvpAndTopKiller()
        {
            var mvp = new WorldEventMvp("Alice", "killing blow", 1);
            var topKiller = Killer("Bob", 7);

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, BuildGoal(), "the gates of Holtburg",
                mvp, topKiller, 12);

            Assert.AreEqual(
                "[World Event] Kill Count at the gates of Holtburg succeeded! Alice struck the final blow. Most kills: Bob (7). 12 defenders took part.",
                line);
        }

        [TestMethod]
        public void OutcomeLine_Success_WithMostDamageMvp()
        {
            var mvp = new WorldEventMvp("Carol", "most damage", 0);

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, BuildGoal(), "the gates of Holtburg",
                mvp, Killer("Dave", 3), 5);

            StringAssert.Contains(line, "Carol dealt the most damage.");
        }

        [TestMethod]
        public void OutcomeLine_Success_UnrecognisedReasonFallsBackToTheRawText()
        {
            var mvp = new WorldEventMvp("Eve", "captured the rift", 0);

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.Success, BuildGoal(), "the gates of Holtburg",
                mvp, null, 1);

            StringAssert.Contains(line, "Eve captured the rift.");
        }

        // ---- OutcomeLine: Failed* ------------------------------------------------------------------------

        [TestMethod]
        public void OutcomeLine_FailedTimeout()
        {
            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedTimeout, BuildGoal(), "the gates of Holtburg",
                WorldEventMvp.None, null, 3);

            Assert.AreEqual(
                "[World Event] Kill Count at the gates of Holtburg failed - time ran out. A consolation cache remains for a short while.",
                line);
        }

        [TestMethod]
        public void OutcomeLine_FailedWipe()
        {
            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedWipe, BuildGoal(), "the gates of Holtburg",
                WorldEventMvp.None, null, 3);

            Assert.AreEqual(
                "[World Event] Kill Count at the gates of Holtburg failed - the defenders were wiped out. A consolation cache remains for a short while.",
                line);
        }

        [TestMethod]
        public void OutcomeLine_FailedNoParticipants()
        {
            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.FailedNoParticipants, BuildGoal(), "the gates of Holtburg",
                WorldEventMvp.None, null, 0);

            Assert.AreEqual(
                "[World Event] Kill Count at the gates of Holtburg failed - nobody answered the call. A consolation cache remains for a short while.",
                line);
        }

        // ---- OutcomeLine: Aborted* -----------------------------------------------------------------------

        [TestMethod]
        public void OutcomeLine_AbortedVariants_AllProduceTheSameCancelledLine()
        {
            var expected = "[World Event] The event at the gates of Holtburg was cancelled.";

            Assert.AreEqual(expected, WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.AbortedAdmin, BuildGoal(),
                "the gates of Holtburg", WorldEventMvp.None, null, 0));

            Assert.AreEqual(expected, WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.AbortedShutdown, BuildGoal(),
                "the gates of Holtburg", WorldEventMvp.None, null, 0));

            Assert.AreEqual(expected, WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.AbortedError, BuildGoal(),
                "the gates of Holtburg", WorldEventMvp.None, null, 0));
        }
    }
}
