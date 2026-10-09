using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

using static ACE.Server.Command.Handlers.PvpArenaCommands;
using AdminCommands = ACE.Server.Command.Handlers.PvpArenaAdminCommands;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Step 6 of Docs/Pvp/BATTLEGROUNDS.md: the player and admin command surface for the battleground room, its
    /// labels, the Crier line and the IP-limit landblock carrier. Pure statics only (no Session, no live Player);
    /// the coordinator-side pieces (clearqueue, the Crier snapshot) are in BattlegroundCoordinatorTests.
    /// </summary>
    [TestClass]
    public class BattlegroundCommandsTests
    {
        // ---------------- /arena join bg ----------------

        [TestMethod]
        public void Status_InABattleground_ShowsBothScoresAndTheTarget_ArenaLineUnchanged()
        {
            var bg = new PvpStatusResult(PvpStatusKind.InMatch, BattlegroundModes.KothModeKey, MapName: "Marketplace",
                Remaining: TimeSpan.FromSeconds(420), Scores: new System.Collections.Generic.Dictionary<int, int> { [0] = 120, [1] = 45 }, ScoreTarget: 300);
            Assert.AreEqual("[Arena] You are in a King of the Hill match on Marketplace. 7 minutes remaining. Score: West 120, East 45, first to 300 wins.",
                StatusResultText(bg));

            var arena = new PvpStatusResult(PvpStatusKind.InMatch, "1v1", MapName: "Arena I", Remaining: TimeSpan.FromSeconds(420));
            StringAssert.EndsWith(StatusResultText(arena), "remaining.");
        }

        [TestMethod]
        public void ParseJoinArena_Bg_Ok_NotGroup()
        {
            var r = ParseJoinArena(new[] { "bg" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual(BattlegroundModes.RoomKey, r.ModeKey);
            Assert.IsFalse(r.Duo);
            Assert.IsFalse(r.Group);
        }

        [TestMethod]
        public void ParseJoinArena_BgGroup_Ok_Group()
        {
            var r = ParseJoinArena(new[] { "BG", "Group" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.AreEqual(BattlegroundModes.RoomKey, r.ModeKey);
            Assert.IsFalse(r.Duo);
            Assert.IsTrue(r.Group);
        }

        [TestMethod]
        public void ParseJoinArena_ArenaModeGroup_ParsesOk_RefusalIsDownstream()
        {
            // Same shape as "ffa duo": the parse accepts it and the coordinator refuses it with
            // GroupNotSupportedForMode (pinned in BattlegroundCoordinatorTests), so the player gets the
            // "only the battleground queue takes a group" line, not a bare usage string.
            foreach (var mode in new[] { "1v1", "2v2", "ffa" })
            {
                var r = ParseJoinArena(new[] { mode, "group" });
                Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome, mode);
                Assert.IsTrue(r.Group, mode);
            }
        }

        [TestMethod]
        public void ParseJoinArena_BgDuo_ParsesOk_RefusalIsDownstream()
        {
            var r = ParseJoinArena(new[] { "bg", "duo" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome);
            Assert.IsTrue(r.Duo);
            Assert.IsFalse(r.Group);
        }

        /// <summary>
        /// PvP Template Facets (owner ruling 2026-10-03: battlegrounds are templated): a bg token that is not "duo" or
        /// "group" is the template key, lowercased, in either order with "group". Before templates these were BadUsage.
        /// </summary>
        [TestMethod]
        public void ParseJoinArena_BgOtherToken_IsTheTemplateKey()
        {
            var solo = ParseJoinArena(new[] { "bg", "Duelist" });
            Assert.AreEqual(JoinArenaParseOutcome.Ok, solo.Outcome);
            Assert.AreEqual(BattlegroundModes.RoomKey, solo.ModeKey);
            Assert.IsFalse(solo.Group);
            Assert.AreEqual("duelist", solo.TemplateKey);

            foreach (var args in new[] { new[] { "bg", "group", "mage" }, new[] { "bg", "mage", "GROUP" } })
            {
                var r = ParseJoinArena(args);
                Assert.AreEqual(JoinArenaParseOutcome.Ok, r.Outcome, string.Join(" ", args));
                Assert.IsTrue(r.Group, string.Join(" ", args));
                Assert.AreEqual("mage", r.TemplateKey, string.Join(" ", args));
            }

            Assert.IsNull(ParseJoinArena(new[] { "bg", "group" }).TemplateKey);
        }

        [TestMethod]
        public void ParseJoinArena_BgTwoKeys_BadKeyShape_OrDuoAndGroup_BadUsage()
        {
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "bg", "party", "x" }).Outcome);
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "bg", "bad key!" }).Outcome);
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "bg", "group", "group" }).Outcome);
            Assert.AreEqual(JoinArenaParseOutcome.BadUsage, ParseJoinArena(new[] { "bg", "duo", "group" }).Outcome);
        }

        [TestMethod]
        public void JoinResultText_BgSuccess_UsesTheBattlegroundLabel()
        {
            var result = new PvpJoinResult(PvpJoinRefusal.None, BattlegroundModes.RoomKey, WaitingCount: 3);
            Assert.AreEqual("[Arena] You joined the Battleground queue. Players waiting: 3. Type /arena leave to leave the queue.", JoinResultText(result));
        }

        [TestMethod]
        public void JoinResultText_GroupRefusals_UseTheBattlegroundLines()
        {
            Assert.AreEqual(BattlegroundText.GroupNotSupportedForMode, JoinResultText(new PvpJoinResult(PvpJoinRefusal.GroupNotSupportedForMode, "2v2")));
            Assert.AreEqual("[Battleground] To queue as a group you must be in a fellowship of 2 to 6 players, and every member must be eligible.", JoinResultText(new PvpJoinResult(PvpJoinRefusal.GroupNeedsFellowship, "bg", GroupMax: 6)));
        }

        [TestMethod]
        public void Usage_ListsBg()
        {
            StringAssert.Contains(JoinUsage, "bg [group]");
            StringAssert.Contains(ArenaUsage, "join bg [group]");
            StringAssert.Contains(ArenaUsage, "top <1v1|2v2|tugak|bg>");
            StringAssert.Contains(TopCommandUsage, "<1v1|2v2|tugak|bg>");
        }

        // ---------------- /top bg ----------------

        [TestMethod]
        public void ParseTop_Bg_Ok()
        {
            var r = ParseTop(new[] { "bg", "5" });
            Assert.AreEqual(TopParseOutcome.Ok, r.Outcome);
            Assert.AreEqual("bg", r.ModeKey);
            Assert.AreEqual(5, r.Count);
        }

        [TestMethod]
        public void LadderForMode_Bg_IsTheBattlegroundLadder_ArenaModesUnchanged()
        {
            Assert.AreEqual(BattlegroundModes.LadderKey, LadderForMode("bg"));
            Assert.AreEqual("battleground", LadderForMode("bg"));
            Assert.AreEqual("arena_1v1", LadderForMode("1v1"));
            Assert.AreEqual("arena_ffa", LadderForMode("ffa"));
        }

        [TestMethod]
        public void RatingLineText_WithBattleground_AppendsTheBattlegroundSegment()
        {
            PvpRatingView Ranked(string ladder, int rating, int wins, int losses) =>
                new PvpRatingView(1, ladder, "Someone", rating, rating, wins + losses, wins, losses, 0, rating, null, HasRecord: true);

            var bg = Ranked("battleground", 1530, 3, 1);

            Assert.AreEqual("[Arena] Alice: 1v1 unranked, 2v2 unranked, Tugak Brawl unranked, Battleground 1530 (3-1).", RatingLineText("Alice", null, null, null, bg));
            Assert.AreEqual("[Arena] Alice: 1v1 unranked, 2v2 unranked, Tugak Brawl unranked, Battleground unranked.", RatingLineText("Alice", null, null, null, null));
        }

        // ---------------- labels ----------------

        [TestMethod]
        public void Labels_CoverTheBattlegroundRoomAndMode_ArenaLabelsUnchanged()
        {
            Assert.AreEqual("Battleground", PvpArenaText.ModeLabel(BattlegroundModes.RoomKey));
            Assert.AreEqual("King of the Hill", PvpArenaText.ModeLabel(BattlegroundModes.KothModeKey));
            Assert.AreEqual("Battleground", PvpArenaText.CrierModeLabel(BattlegroundModes.RoomKey));
            Assert.AreEqual("King of the Hill", PvpArenaText.CrierModeLabel(BattlegroundModes.KothModeKey));
            Assert.AreEqual("Tugak Brawl", PvpArenaText.ModeLabel("ffa"));
            Assert.AreEqual("Tugak Brawl", PvpArenaText.CrierModeLabel("ffa"));
        }

        [TestMethod]
        public void Crier_BgSnapshot_AnnouncesTheBattlegroundLineWithTheJoinArg()
        {
            var crier = new PvpArenaCrier();
            var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            var dials = PvpTunables.Defaults with { CrierEnabled = true, CrierAnnounceOnJoin = false, CrierIntervalSeconds = 900 };
            var queues = new[] { new CrierQueueSnapshot(BattlegroundModes.RoomKey, true, 2, 2) };

            crier.Tick(t0, dials, queues);
            var lines = crier.Tick(t0.AddSeconds(900), dials, queues);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("Battleground queue: 2 queued, 2 more needed. Type /arena join bg to play.", lines[0]);
        }

        // ---------------- /arenaadmin ----------------

        [TestMethod]
        public void ClearableModes_IncludeTheBattlegroundRoom_AndEveryRoomKeyThereIs()
        {
            CollectionAssert.Contains(AdminCommands.ClearableModes, BattlegroundModes.RoomKey);
            CollectionAssert.Contains(AdminCommands.ClearableModes, "1v1");
            CollectionAssert.Contains(AdminCommands.ClearableModes, "2v2");
            CollectionAssert.Contains(AdminCommands.ClearableModes, "ffa");
        }

        [TestMethod]
        public void FindTestSpaceMap_Bg016c_ResolvesToTheSpaceBridge_ArenaKeysUnchanged()
        {
            var bg = AdminCommands.FindTestSpaceMap("BG_016C");

            Assert.IsNotNull(bg);
            Assert.AreSame(BattlegroundMapCatalog.Bg016cSpaceMap, bg);
            Assert.AreEqual(BattlegroundMapCatalog.Landblock016c, bg.LandblockId);
            Assert.AreSame(ArenaMapCatalog.Arena0066, AdminCommands.FindTestSpaceMap("arena_0066"));
            Assert.AreSame(ArenaMapCatalog.Arena0067, AdminCommands.FindTestSpaceMap("arena_0067"));
            Assert.IsNull(AdminCommands.FindTestSpaceMap("bg_ffff"));
        }

        [TestMethod]
        public void FindTestSpaceMap_BgMapIsNotInTheArenaCatalog()
        {
            Assert.IsNull(ArenaMapCatalog.Find(BattlegroundMapCatalog.Bg016cKey), "testspace bridges the bg map itself; ArenaMapCatalog stays arena-only");
        }

        [TestMethod]
        public void TestSpaceSpawn_BgMap_IsAWestTeamSpawn()
        {
            var spawn = AdminCommands.TestSpaceSpawn(BattlegroundMapCatalog.Bg016cSpaceMap);

            Assert.IsNotNull(spawn);
            Assert.AreEqual(BattlegroundMapCatalog.Bg016c.TeamSpawns[0][0], spawn);
        }

        // ---------------- IP-limit carrier ----------------

        [TestMethod]
        public void IsPvpMapLandblock_CoversArenasAndTheBattleground_NotTheOpenWorld()
        {
            Assert.IsTrue(PvpMatchLandblocks.IsPvpMapLandblock(0x0066));
            Assert.IsTrue(PvpMatchLandblocks.IsPvpMapLandblock(0x0067));
            Assert.IsTrue(PvpMatchLandblocks.IsPvpMapLandblock(0x016C), "a battleground player is confined by the IP limit like an arena player");
            Assert.IsFalse(PvpMatchLandblocks.IsPvpMapLandblock(0x01F5), "the Marketplace is not a PvP map");
            Assert.IsFalse(PvpMatchLandblocks.IsPvpMapLandblock(0));
        }

        [TestMethod]
        public void IsInPvpArenaMatchSpace_AsksTheSharedLandblockHelper()
        {
            var body = Squash(PropertyBody("Player_PvpArena.cs", "public bool IsInPvpArenaMatchSpace"));

            StringAssert.Contains(body, Squash("PvpMatchLandblocks.IsPvpMapLandblock(Location.LandblockShort)"));
            Assert.IsFalse(body.Contains("ArenaMapCatalog.All"), "the carrier must not go back to the arena-only list");
        }

        private static string Squash(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

        /// <summary>The text from <paramref name="signature"/> to the next semicolon (an expression-bodied property).</summary>
        private static string PropertyBody(string fileName, string signature)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;

            while (dir != null && path == null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", fileName);

                if (File.Exists(candidate))
                    path = candidate;

                dir = dir.Parent;
            }

            Assert.IsNotNull(path, $"Could not find {fileName} by walking up from {AppContext.BaseDirectory}.");

            var text = File.ReadAllText(path);
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{signature} not found in {fileName}");

            var end = text.IndexOf(';', start);
            return text.Substring(start, end - start + 1);
        }
    }
}