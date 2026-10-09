using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

using static ACE.Server.Command.Handlers.PvpArenaAdminCommands;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure statics behind /arenaadmin: short-id formatting and prefix matching (including ambiguity), and the
    /// admin list row format. No live Player/Session/Landblock anywhere here.
    /// </summary>
    [TestClass]
    public class PvpArenaAdminCommandsTests
    {
        // ---------------- ShortId ----------------

        [TestMethod]
        public void ShortId_IsFirstEightHexCharsOfNForm()
        {
            var id = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            Assert.AreEqual("ab12cd34", ShortId(id));
        }

        // ---------------- MatchShortId ----------------

        [TestMethod]
        public void MatchShortId_UniquePrefix_ReturnsOk()
        {
            var a = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var b = Guid.Parse("ffffffff-0000-0000-0000-000000000000");
            var ids = new List<Guid> { a, b };

            var (outcome, matchId) = MatchShortId(ids, "ab12");

            Assert.AreEqual(ShortIdMatchOutcome.Ok, outcome);
            Assert.AreEqual(a, matchId);
        }

        [TestMethod]
        public void MatchShortId_IsCaseInsensitive()
        {
            var a = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var (outcome, matchId) = MatchShortId(new List<Guid> { a }, "AB12CD34");

            Assert.AreEqual(ShortIdMatchOutcome.Ok, outcome);
            Assert.AreEqual(a, matchId);
        }

        [TestMethod]
        public void MatchShortId_FullGuid_Matches()
        {
            var a = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var (outcome, matchId) = MatchShortId(new List<Guid> { a }, a.ToString("N"));

            Assert.AreEqual(ShortIdMatchOutcome.Ok, outcome);
            Assert.AreEqual(a, matchId);
        }

        [TestMethod]
        public void MatchShortId_NoMatch_ReturnsNotFound()
        {
            var a = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var (outcome, _) = MatchShortId(new List<Guid> { a }, "zzzz");

            Assert.AreEqual(ShortIdMatchOutcome.NotFound, outcome);
        }

        [TestMethod]
        public void MatchShortId_AmbiguousPrefix_ReturnsAmbiguous()
        {
            // Two ids that share the first 4 hex characters of their ShortId.
            var a = Guid.Parse("ab120001-0000-0000-0000-000000000000");
            var b = Guid.Parse("ab120002-0000-0000-0000-000000000000");

            var (outcome, _) = MatchShortId(new List<Guid> { a, b }, "ab12");

            Assert.AreEqual(ShortIdMatchOutcome.Ambiguous, outcome);
        }

        [TestMethod]
        public void MatchShortId_EmptyPrefix_ReturnsNotFound()
        {
            var a = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var (outcome, _) = MatchShortId(new List<Guid> { a }, "");

            Assert.AreEqual(ShortIdMatchOutcome.NotFound, outcome);
        }

        [TestMethod]
        public void MatchShortId_NullList_ReturnsNotFound()
        {
            var (outcome, _) = MatchShortId(null, "ab12");
            Assert.AreEqual(ShortIdMatchOutcome.NotFound, outcome);
        }

        // ---------------- FormatElapsed ----------------

        [TestMethod]
        public void FormatElapsed_UnderAMinute()
        {
            Assert.AreEqual("0:09", FormatElapsed(TimeSpan.FromSeconds(9)));
        }

        [TestMethod]
        public void FormatElapsed_OverAMinute_PadsSeconds()
        {
            Assert.AreEqual("2:05", FormatElapsed(TimeSpan.FromSeconds(125)));
        }

        [TestMethod]
        public void FormatElapsed_Negative_ClampsToZero()
        {
            Assert.AreEqual("0:00", FormatElapsed(TimeSpan.FromSeconds(-3)));
        }

        // ---------------- FormatMatchLine ----------------

        [TestMethod]
        public void FormatMatchLine_IncludesShortIdModeMapStateParticipantsAndElapsed()
        {
            var id = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var summary = new PvpMatchSummary(id, "1v1", PvpMatchState.Live, "arena_0066", "Arena I", 0x80010001,
                new List<string> { "Alice", "Bob" }, TimeSpan.FromSeconds(65));

            var line = FormatMatchLine(summary);

            Assert.AreEqual("  ab12cd34  1v1  Arena I  Live  [Alice, Bob]  1:05", line);
        }

        [TestMethod]
        public void FormatMatchLine_NoParticipants_ShowsDash()
        {
            var id = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var summary = new PvpMatchSummary(id, "ffa", PvpMatchState.Staging, "arena_0067", "Arena II", null,
                new List<string>(), TimeSpan.Zero);

            var line = FormatMatchLine(summary);

            Assert.AreEqual("  ab12cd34  tugak  Arena II  Staging  [-]  0:00", line, "the admin list shows the word an admin types for the mode, not the internal key");
        }

        [TestMethod]
        public void FormatMatchLine_NoMapName_FallsBackToMapKey()
        {
            var id = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");
            var summary = new PvpMatchSummary(id, "2v2", PvpMatchState.AwaitingAccept, "arena_0066", null, null,
                new List<string> { "Carol" }, TimeSpan.Zero);

            var line = FormatMatchLine(summary);

            Assert.AreEqual("  ab12cd34  2v2  arena_0066  AwaitingAccept  [Carol]  0:00", line);
        }

        // ---------------- TestSpaceSpawn ----------------
        //
        // Pure, so it is covered directly; the try/catch this review pass also added around HandleTestSpace's
        // Teleport call (PvpArenaAdminCommands.cs) is not reachable this cheaply - Player.Teleport needs a live
        // WorldObject/Landblock the test tree does not build. Not covered here.

        private static ArenaMap MapWithFfaSet(params PvpSpawnPoint[] points)
        {
            var sets = new Dictionary<string, IReadOnlyList<PvpSpawnPoint>>
            {
                [ArenaMapCatalog.FfaKey] = points,
            };

            return new ArenaMap("arena_test", 0x0066, ArenaMapCatalog.ArenaRealmId, sets);
        }

        [TestMethod]
        public void TestSpaceSpawn_F1Present_ReturnsF1()
        {
            var f1 = new PvpSpawnPoint("F1", 0x0120, 50f, -25f, 0.005f, 0.707107f, 0.707107f);
            var f2 = new PvpSpawnPoint("F2", 0x011F, 47.32f, -15f, 0.005f, 0.5f, 0.866025f);
            var map = MapWithFfaSet(f2, f1);

            Assert.AreEqual(f1, TestSpaceSpawn(map));
        }

        [TestMethod]
        public void TestSpaceSpawn_NoF1Label_FallsBackToFirstPoint()
        {
            var only = new PvpSpawnPoint("F7", 0x0108, 10f, -25f, 0.005f, 0.707107f, -0.707107f);
            var map = MapWithFfaSet(only);

            Assert.AreEqual(only, TestSpaceSpawn(map));
        }

        [TestMethod]
        public void TestSpaceSpawn_EmptySet_ReturnsNull()
        {
            var map = MapWithFfaSet();

            Assert.IsNull(TestSpaceSpawn(map));
        }

        [TestMethod]
        public void TestSpaceSpawn_NullMap_ReturnsNull()
        {
            Assert.IsNull(TestSpaceSpawn(null));
        }

        [TestMethod]
        public void TestSpaceSpawn_RealCatalogMaps_ReturnF1()
        {
            // Confirms the fix reads from the MAP's own set (SpawnPointsFor) and lands on the same F1 the old
            // hardcoded ArenaMapCatalog.FfaSet.First(p => p.Label == "F1") used, for both shipped v1 maps.
            var expectedF1 = ArenaMapCatalog.FfaSet[0];
            Assert.AreEqual("F1", expectedF1.Label);

            Assert.AreEqual(expectedF1, TestSpaceSpawn(ArenaMapCatalog.Arena0066));
            Assert.AreEqual(expectedF1, TestSpaceSpawn(ArenaMapCatalog.Arena0067));
        }
    }
}
