using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

using static ACE.Server.Command.Handlers.PvpArenaCommands;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure statics behind the /arena join and /arena player commands: argument parsing, mode/count clamping,
    /// and every coordinator result mapped to its chat line - all testable with no Session and no live Player.
    /// </summary>
    [TestClass]
    public class PvpArenaCommandsTests
    {
        // ---------------- ParseJoinArena ----------------

        [TestMethod]
        public void ParseJoinArena_OneVOne_Ok()
        {
            var r = ParseJoinArena(new[] { "1v1" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("1v1", r.ModeKey);
            Assert.IsFalse(r.Duo);
        }

        [TestMethod]
        public void ParseJoinArena_TwoVTwo_Ok()
        {
            var r = ParseJoinArena(new[] { "2v2" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("2v2", r.ModeKey);
            Assert.IsFalse(r.Duo);
        }

        [TestMethod]
        public void ParseJoinArena_TwoVTwoDuo_Ok()
        {
            var r = ParseJoinArena(new[] { "2v2", "duo" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("2v2", r.ModeKey);
            Assert.IsTrue(r.Duo);
        }

        [TestMethod]
        public void ParseJoinArena_Ffa_Ok()
        {
            var r = ParseJoinArena(new[] { "ffa" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("ffa", r.ModeKey);
        }

        [TestMethod]
        public void ParseJoinArena_FfaDuo_ParsesOk_DuoRefusalIsDownstream()
        {
            // Parsing accepts "ffa duo" - whether duo is supported for the mode is a coordinator refusal
            // (DuoNotSupportedForMode), not a parse error.
            var r = ParseJoinArena(new[] { "ffa", "duo" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.IsTrue(r.Duo);
        }

        [TestMethod]
        public void ParseJoinArena_IsCaseInsensitive()
        {
            var r = ParseJoinArena(new[] { "1V1" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("1v1", r.ModeKey);
        }

        [TestMethod]
        public void ParseJoinArena_UnknownMode_BadUsage()
        {
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "3v3" }).Outcome);
        }

        /// <summary>PvP Template Facets: a second token that is not "duo" is the template key, lowercased.</summary>
        [TestMethod]
        public void ParseJoinArena_SecondTokenIsTheTemplateKey()
        {
            var r = ParseJoinArena(new[] { "2v2", "Duelist" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("2v2", r.ModeKey);
            Assert.IsFalse(r.Duo);
            Assert.AreEqual("duelist", r.TemplateKey);
        }

        [TestMethod]
        public void ParseJoinArena_DuoAndKey_EitherOrder()
        {
            foreach (var args in new[] { new[] { "2v2", "duo", "mage" }, new[] { "2v2", "mage", "DUO" } })
            {
                var r = ParseJoinArena(args);
                Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome, string.Join(" ", args));
                Assert.IsTrue(r.Duo);
                Assert.AreEqual("mage", r.TemplateKey);
            }
        }

        [TestMethod]
        public void ParseJoinArena_NoKey_LeavesTheKeyNull()
        {
            Assert.IsNull(ParseJoinArena(new[] { "1v1" }).TemplateKey);
            Assert.IsNull(ParseJoinArena(new[] { "2v2", "duo" }).TemplateKey);
        }

        [TestMethod]
        public void ParseJoinArena_BadKeyShape_OrTwoKeys_OrTwoDuos_BadUsage()
        {
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "1v1", "bad key!" }).Outcome);
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "1v1", "a", "b" }).Outcome);
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "2v2", "duo", "duo" }).Outcome);
        }

        [TestMethod]
        public void ParseJoinArena_NoArgs_BadUsage()
        {
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(Array.Empty<string>()).Outcome);
        }

        [TestMethod]
        public void ParseJoinArena_TooManyArgs_BadUsage()
        {
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "1v1", "duo", "extra", "more" }).Outcome);
        }

        [TestMethod]
        public void ParseJoinArena_Null_BadUsage()
        {
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(null).Outcome);
        }

        // ---------------- /arena join entry point ----------------
        //
        // HandleArena's "join" case strips the leading "join" token off its raw parameters and hands the
        // remainder straight to ParseJoinArena via HandleJoinArena(player, args) - no further transformation - so
        // every parse form above (ParseJoinArena_*) already covers "/arena join ...". These two pin the
        // /arena-specific usage text.

        [TestMethod]
        public void JoinUsage_PointsAtArenaJoin()
        {
            Assert.AreEqual("[Arena] Usage: /arena join <1v1|2v2|tugak> [duo] [template] or /arena join bg [group] [template]", JoinUsage);
        }

        [TestMethod]
        public void ArenaUsage_ListsJoin()
        {
            StringAssert.Contains(ArenaUsage, "join <1v1|2v2|tugak> [duo] [template]");
        }

        // ---------------- JoinResultText ----------------

        [TestMethod]
        public void JoinResultText_Success_UsesJoinedTemplate()
        {
            var result = new PvpJoinResult(PvpJoinRefusal.None, "1v1", WaitingCount: 2);
            Assert.AreEqual("[Arena] You joined the 1v1 queue. Players waiting: 2. Type /arena leave to leave the queue.", JoinResultText(result));
        }

        [TestMethod]
        public void JoinResultText_DuoSuccess_UsesJoinedDuoTemplate()
        {
            var result = new PvpJoinResult(PvpJoinRefusal.None, "2v2", WaitingCount: 4, PartnerName: "Bob");
            Assert.AreEqual("[Arena] You and Bob joined the 2v2 queue as a pair. Players waiting: 4.", JoinResultText(result));
        }

        [TestMethod]
        public void JoinResultText_Refusal_UsesCoordinatorMapper()
        {
            var result = new PvpJoinResult(PvpJoinRefusal.AlreadyInMatch, "1v1");
            Assert.AreEqual(PvpArenaText.AlreadyInMatch, JoinResultText(result));
        }

        [TestMethod]
        public void JoinResultText_BelowMinLevel_FillsLevel()
        {
            var result = new PvpJoinResult(PvpJoinRefusal.BelowMinLevel, "1v1", MinLevel: 50);
            Assert.AreEqual("[Arena] You must be at least level 50 to enter the arena.", JoinResultText(result));
        }

        // ---------------- ForfeitResultText ----------------

        [TestMethod]
        public void ForfeitResultText_Forfeited_Null_CoordinatorAlreadySent()
        {
            Assert.IsNull(ForfeitResultText(new PvpForfeitResult(PvpForfeitOutcome.Forfeited)));
        }

        [TestMethod]
        public void ForfeitResultText_Declined_Null_CoordinatorAlreadySent()
        {
            Assert.IsNull(ForfeitResultText(new PvpForfeitResult(PvpForfeitOutcome.Declined)));
        }

        [TestMethod]
        public void ForfeitResultText_NotInMatch_ReturnsNotQueuedOrInMatch()
        {
            Assert.AreEqual(PvpArenaText.NotQueuedOrInMatch, ForfeitResultText(new PvpForfeitResult(PvpForfeitOutcome.NotInMatch)));
        }

        [TestMethod]
        public void ForfeitResultText_AlreadyOut_ReturnsAlreadyOutOfMatch()
        {
            Assert.AreEqual(PvpArenaText.AlreadyOutOfMatch, ForfeitResultText(new PvpForfeitResult(PvpForfeitOutcome.AlreadyOut)));
        }

        // ---------------- StatusResultText ----------------

        [TestMethod]
        public void StatusResultText_Idle_UsesStatusIdle()
        {
            Assert.AreEqual(PvpArenaText.StatusIdle, StatusResultText(new PvpStatusResult(PvpStatusKind.Idle)));
        }

        [TestMethod]
        public void StatusResultText_Queued_FillsModeWaitedCount()
        {
            var result = new PvpStatusResult(PvpStatusKind.Queued, "ffa", TimeSpan.FromSeconds(90), 7);
            Assert.AreEqual("[Arena] You are in the Tugak Brawl queue (1 minute waited). Players waiting: 7.", StatusResultText(result));
        }

        [TestMethod]
        public void StatusResultText_InMatch_WithRemaining_FillsTemplate()
        {
            var result = new PvpStatusResult(PvpStatusKind.InMatch, "1v1", MapName: "Arena I", Remaining: TimeSpan.FromSeconds(125));
            Assert.AreEqual("[Arena] You are in a 1v1 match on Arena I. 2 minutes remaining.", StatusResultText(result));
        }

        [TestMethod]
        public void StatusResultText_InMatch_Overtime_SaysOvertime()
        {
            var result = new PvpStatusResult(PvpStatusKind.InMatch, "1v1", MapName: "Arena I", Remaining: TimeSpan.FromSeconds(45), Overtime: true);
            Assert.AreEqual("[Arena] You are in a 1v1 match on Arena I. Overtime: 45 seconds remaining.", StatusResultText(result));
        }

        [TestMethod]
        public void StatusResultText_InMatch_WithoutRemaining_UsesNotLiveTemplate()
        {
            var result = new PvpStatusResult(PvpStatusKind.InMatch, "2v2", MapName: "Arena II", Remaining: null);
            Assert.AreEqual("[Arena] You are in a 2v2 match on Arena II. It has not started yet.", StatusResultText(result));
        }

        [TestMethod]
        public void StatusResultText_AwaitingAccept_FillsModeAndMap()
        {
            var result = new PvpStatusResult(PvpStatusKind.AwaitingAccept, "1v1", MapName: "Arena I");
            Assert.AreEqual("[Arena] Your 1v1 match is ready on Arena I. Type /arena accept or /arena decline.", StatusResultText(result));
        }

        [TestMethod]
        public void StatusResultText_AwaitingAccept_NoMapName_FallsBackToMapKey()
        {
            var result = new PvpStatusResult(PvpStatusKind.AwaitingAccept, "1v1", MapKey: "arena_0066", MapName: null);
            Assert.AreEqual("[Arena] Your 1v1 match is ready on arena_0066. Type /arena accept or /arena decline.", StatusResultText(result));
        }

        [TestMethod]
        public void MapDisplay_NoMapNameOrKey_FallsBackToTheArena()
        {
            var result = new PvpStatusResult(PvpStatusKind.AwaitingAccept, "1v1");
            Assert.AreEqual("the arena", MapDisplay(result));
        }

        // ---------------- FormatDuration ----------------

        [TestMethod]
        public void FormatDuration_RoundsSecondsUp()
        {
            Assert.AreEqual("1 second", FormatDuration(TimeSpan.FromMilliseconds(400)));
            Assert.AreEqual("30 seconds", FormatDuration(TimeSpan.FromSeconds(29.2)));
        }

        [TestMethod]
        public void FormatDuration_RoundsMinutesDown()
        {
            // 65 rounded-up seconds is 65 seconds, which is >= 60, so it reads as 1 minute (not 2).
            Assert.AreEqual("1 minute", FormatDuration(TimeSpan.FromSeconds(65)));
            Assert.AreEqual("9 minutes", FormatDuration(TimeSpan.FromSeconds(599)));
        }

        [TestMethod]
        public void FormatDuration_Negative_ClampsToZero()
        {
            Assert.AreEqual("0 seconds", FormatDuration(TimeSpan.FromSeconds(-5)));
        }

        // ---------------- AnswerResultText ----------------

        [TestMethod]
        public void AnswerResultText_Accepted_Null_NothingFurtherToSay()
        {
            Assert.IsNull(AnswerResultText(new PvpAnswerResult(PvpAnswerOutcome.Accepted), accepted: true));
        }

        [TestMethod]
        public void AnswerResultText_Declined_Null_CoordinatorAlreadySent()
        {
            Assert.IsNull(AnswerResultText(new PvpAnswerResult(PvpAnswerOutcome.Declined), accepted: false));
        }

        [TestMethod]
        public void AnswerResultText_NoPendingMatch_ReturnsNoPendingMatch()
        {
            Assert.AreEqual(PvpArenaText.NoPendingMatch, AnswerResultText(new PvpAnswerResult(PvpAnswerOutcome.NoPendingMatch), true));
        }

        [TestMethod]
        public void AnswerResultText_AlreadyAnswered_ReturnsAlreadyAnswered()
        {
            Assert.AreEqual(PvpArenaText.AlreadyAnswered, AnswerResultText(new PvpAnswerResult(PvpAnswerOutcome.AlreadyAnswered), true));
        }

        // ---------------- RatingLineText ----------------

        private static PvpRatingView Ranked(uint id, string ladder, int rating, int wins, int losses, int games) =>
            new PvpRatingView(id, ladder, "Someone", rating, rating, games, wins, losses, 0, rating, null, HasRecord: true);

        private static PvpRatingView Unranked(uint id, string ladder) =>
            new PvpRatingView(id, ladder, null, 1500, 1500, 0, 0, 0, 0, 1500, null, HasRecord: false);

        [TestMethod]
        public void RatingLineText_AllRanked_ShowsEachLadder()
        {
            var oneVOne = Ranked(1, "arena_1v1", 1550, 4, 2, 6);
            var twoVTwo = Ranked(1, "arena_2v2", 1490, 1, 3, 4);
            var ffa = Ranked(1, "arena_ffa", 1610, 10, 1, 11);

            var text = RatingLineText("Alice", oneVOne, twoVTwo, ffa);

            Assert.AreEqual("[Arena] Alice: 1v1 1550 (4-2), 2v2 1490 (1-3), Tugak Brawl 1610 (10-1).", text);
        }

        [TestMethod]
        public void RatingLineText_AllUnranked_ShowsUnrankedForEach()
        {
            var text = RatingLineText("Alice", Unranked(1, "arena_1v1"), Unranked(1, "arena_2v2"), Unranked(1, "arena_ffa"));

            Assert.AreEqual("[Arena] Alice: 1v1 unranked, 2v2 unranked, Tugak Brawl unranked.", text);
        }

        [TestMethod]
        public void RatingLineText_Mixed_UnrankedNeverLeavesAStrayScore()
        {
            var oneVOne = Ranked(1, "arena_1v1", 1550, 4, 2, 6);
            var text = RatingLineText("Alice", oneVOne, Unranked(1, "arena_2v2"), Unranked(1, "arena_ffa"));

            Assert.AreEqual("[Arena] Alice: 1v1 1550 (4-2), 2v2 unranked, Tugak Brawl unranked.", text);
        }

        [TestMethod]
        public void RatingLineText_NullView_IsUnranked()
        {
            var text = RatingLineText("Alice", null, null, null);
            Assert.AreEqual("[Arena] Alice: 1v1 unranked, 2v2 unranked, Tugak Brawl unranked.", text);
        }

        // ---------------- ParseTop ----------------

        [TestMethod]
        public void ParseTop_ModeOnly_DefaultsToTen()
        {
            var r = ParseTop(new[] { "1v1" });
            Assert.AreEqual(TopParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("1v1", r.ModeKey);
            Assert.AreEqual(10, r.Count);
        }

        [TestMethod]
        public void ParseTop_ExplicitCount_Used()
        {
            var r = ParseTop(new[] { "2v2", "5" });
            Assert.AreEqual(TopParseOutcome.Ok, r.Outcome);
            Assert.AreEqual(5, r.Count);
        }

        [TestMethod]
        public void ParseTop_CountAboveMax_ClampsToTwentyFive()
        {
            var r = ParseTop(new[] { "ffa", "100" });
            Assert.AreEqual(TopParseOutcome.Ok, r.Outcome);
            Assert.AreEqual(25, r.Count);
        }

        [TestMethod]
        public void ParseTop_CountExactlyMax_Unchanged()
        {
            var r = ParseTop(new[] { "ffa", "25" });
            Assert.AreEqual(25, r.Count);
        }

        [TestMethod]
        public void ParseTop_ZeroCount_BadUsage()
        {
            Assert.AreEqual(TopParseOutcome.BadUsage, ParseTop(new[] { "1v1", "0" }).Outcome);
        }

        [TestMethod]
        public void ParseTop_NegativeCount_BadUsage()
        {
            Assert.AreEqual(TopParseOutcome.BadUsage, ParseTop(new[] { "1v1", "-5" }).Outcome);
        }

        [TestMethod]
        public void ParseTop_NonNumericCount_BadUsage()
        {
            Assert.AreEqual(TopParseOutcome.BadUsage, ParseTop(new[] { "1v1", "abc" }).Outcome);
        }

        [TestMethod]
        public void ParseTop_UnknownMode_BadUsage()
        {
            Assert.AreEqual(TopParseOutcome.BadUsage, ParseTop(new[] { "3v3" }).Outcome);
        }

        [TestMethod]
        public void ParseTop_NoArgs_BadUsage()
        {
            Assert.AreEqual(TopParseOutcome.BadUsage, ParseTop(Array.Empty<string>()).Outcome);
        }

        [TestMethod]
        public void ParseTop_TooManyArgs_BadUsage()
        {
            Assert.AreEqual(TopParseOutcome.BadUsage, ParseTop(new[] { "1v1", "5", "extra" }).Outcome);
        }

        // ---------------- LadderForMode / FillTopRow ----------------

        [TestMethod]
        public void LadderForMode_MatchesPvpModesHardcodedKeys()
        {
            Assert.AreEqual("arena_1v1", LadderForMode("1v1"));
            Assert.AreEqual("arena_2v2", LadderForMode("2v2"));
            Assert.AreEqual("arena_ffa", LadderForMode("ffa"));
        }

        [TestMethod]
        public void FillTopRow_MatchesTemplate()
        {
            Assert.AreEqual("1. Alice 1600 (5-1)", FillTopRow(1, "Alice", 1600, 5, 1));
        }

        /// <summary>PvP Template Facets: /top shows the template of the player's latest match beside their name.</summary>
        [TestMethod]
        public void FillTopRow_WithTemplate_ShowsItBesideTheName()
        {
            Assert.AreEqual("1. Alice (Duelist) 1600 (5-1)", FillTopRow(1, "Alice", 1600, 5, 1, "Duelist"));
        }

        // ---------------- PvP Template Facets: join and template lines ----------------

        [TestMethod]
        public void JoinResultText_WithTemplate_NamesIt()
        {
            var result = new PvpJoinResult(PvpJoinRefusal.None, "1v1", WaitingCount: 2, TemplateKey: "duelist", TemplateLabel: "Duelist");
            Assert.AreEqual("[Arena] You joined the 1v1 queue on the Duelist template. Players waiting: 2. Type /arena leave to leave the queue.", JoinResultText(result));
        }

        [TestMethod]
        public void JoinResultText_TemplateRefusals_MapToTheirLines()
        {
            Assert.AreEqual(PvpArenaText.NoTemplateChosen, JoinResultText(new PvpJoinResult(PvpJoinRefusal.NoTemplateChosen, "1v1")));
            Assert.AreEqual("[Arena] The mage template is not offered for 2v2. Type /arena templates to see what is.", JoinResultText(new PvpJoinResult(PvpJoinRefusal.TemplateNotOffered, "2v2", TemplateKey: "mage")));
            Assert.AreEqual("room line", JoinResultText(new PvpJoinResult(PvpJoinRefusal.TemplateNoRoom, "1v1", Detail: "room line")));
            Assert.AreEqual(PvpArenaText.TemplateAccountRefused, JoinResultText(new PvpJoinResult(PvpJoinRefusal.TemplateAccount, "1v1")));
            Assert.AreEqual(PvpArenaText.TemplateLockedRefused, JoinResultText(new PvpJoinResult(PvpJoinRefusal.TemplateLocked, "1v1")));
            Assert.AreEqual(PvpArenaText.TemplatesDisabled, JoinResultText(new PvpJoinResult(PvpJoinRefusal.TemplatesDisabled, "1v1")));
            Assert.AreEqual(PvpArenaText.TemplatesUnavailable, JoinResultText(new PvpJoinResult(PvpJoinRefusal.TemplatesUnavailable, "1v1")));
        }

        [TestMethod]
        public void TemplatesListText_ListsModes_AndNamesTheCurrentChoice()
        {
            var offered = new List<PvpTemplateOffer>
            {
                new PvpTemplateOffer("duelist", "Duelist", 2, true, new[] { "1v1", "ffa" }, true),
                new PvpTemplateOffer("mage", "War Mage", 1, true, new[] { "2v2" }, true),
            };

            var text = TemplatesListText(offered, "mage", key => key == "mage" ? "War Mage" : key);

            Assert.AreEqual(
                "[Arena] Arena templates:" + Environment.NewLine +
                "  duelist - Duelist (1v1, Tugak Brawl)" + Environment.NewLine +
                "  mage - War Mage (2v2)" + Environment.NewLine +
                "[Arena] Choose one with /arena template <key>, or join directly with /arena join <mode> <key>. Your template: War Mage.",
                text);

            Assert.AreEqual(PvpArenaText.TemplatesNone, TemplatesListText(new List<PvpTemplateOffer>(), null, null));
        }
    }
}
