using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the pure helpers behind /removespeedrun: the tail-anchored argument parser (a character
    /// name can contain spaces, so the confirm form is recognised from the END of the parameter list,
    /// not from a fixed count) and the best-row selection that identifies which of a character's rows
    /// currently backs their board entry. Runs with no database, no session and no live world.
    /// </summary>
    [TestClass]
    public class SpeedSeasonAdminCommandsTests
    {
        private static CharacterSpeedRun MakeRow(uint id, uint characterId, string characterName, int seasonId, long centiseconds, DateTime completedAt, int level = 100)
        {
            return new CharacterSpeedRun
            {
                Id = id,
                CharacterId = characterId,
                CharacterName = characterName,
                SeasonId = seasonId,
                Centiseconds = centiseconds,
                CharacterLevel = level,
                CompletedAt = completedAt,
            };
        }

        private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        // =======================================================================================
        // TryParseRemoveSpeedRunArgs
        // =======================================================================================

        [TestMethod]
        public void TryParse_DryRunForm_SimpleName_Parses()
        {
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "Bob" }, out var args, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual(4, args.SeasonId);
            Assert.AreEqual("Bob", args.CharacterName);
            Assert.IsNull(args.RunId);
            Assert.IsFalse(args.Confirmed);
        }

        [TestMethod]
        public void TryParse_DryRunForm_MultiWordName_JoinsTheWholeTail()
        {
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "John", "Doe" }, out var args, out var error);

            Assert.IsTrue(ok);
            Assert.AreEqual("John Doe", args.CharacterName);
            Assert.IsFalse(args.Confirmed);
        }

        [TestMethod]
        public void TryParse_ConfirmForm_SimpleName_Parses()
        {
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "Bob", "17", "confirm" }, out var args, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual(4, args.SeasonId);
            Assert.AreEqual("Bob", args.CharacterName);
            Assert.AreEqual(17u, args.RunId);
            Assert.IsTrue(args.Confirmed);
        }

        [TestMethod]
        public void TryParse_ConfirmForm_MultiWordName_JoinsOnlyTheNamePortion()
        {
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "John", "Doe", "17", "confirm" }, out var args, out var error);

            Assert.IsTrue(ok);
            Assert.AreEqual("John Doe", args.CharacterName, "the run id and 'confirm' must not be swallowed into the name");
            Assert.AreEqual(17u, args.RunId);
            Assert.IsTrue(args.Confirmed);
        }

        [TestMethod]
        public void TryParse_ConfirmForm_IsCaseInsensitiveOnTheConfirmToken()
        {
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "Bob", "17", "CONFIRM" }, out var args, out _);

            Assert.IsTrue(ok);
            Assert.IsTrue(args.Confirmed);
        }

        [TestMethod]
        public void TryParse_TooFewParameters_Fails()
        {
            Assert.IsFalse(SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4" }, out _, out var error));
            Assert.IsNotNull(error);

            Assert.IsFalse(SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new string[0], out _, out error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void TryParse_NullParameters_FailsRatherThanThrowing()
        {
            Assert.IsFalse(SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(null, out _, out var error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void TryParse_NonNumericSeason_Fails()
        {
            Assert.IsFalse(SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "abc", "Bob" }, out _, out var error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void TryParse_ConfirmTailWithNonNumericRunId_FallsBackToDryRunReadingOfTheWholeTail()
        {
            // "confirm" as the very last word with a non-numeric token before it is not a valid confirm
            // form (no run id), so this must not be silently accepted as EITHER form with a wrong
            // interpretation - it must fail closed rather than guess a run id.
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "Bob", "abc", "confirm" }, out _, out var error);

            Assert.IsFalse(ok, "a malformed confirm tail must be rejected, never guessed at");
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void TryParse_OnlySeasonAndConfirm_TooFewTokensForConfirmForm_TreatsConfirmAsPartOfTheName()
        {
            // Below the 3-token minimum for a confirm tail (name + runId + "confirm"), so this is read as
            // the dry-run form with a two-word name. Documents the boundary rather than asserting it is
            // the only sensible reading.
            var ok = SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "Bob", "confirm" }, out var args, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual("Bob confirm", args.CharacterName);
            Assert.IsFalse(args.Confirmed);
        }

        [TestMethod]
        public void TryParse_BlankCharacterName_Fails()
        {
            Assert.IsFalse(SpeedSeasonAdminCommands.TryParseRemoveSpeedRunArgs(new[] { "4", "   " }, out _, out var error));
            Assert.IsNotNull(error);
        }

        // =======================================================================================
        // SelectCurrentBoardRow
        // =======================================================================================

        [TestMethod]
        public void SelectCurrentBoardRow_SingleRow_IsItself()
        {
            var row = MakeRow(1, 100, "Bob", 4, 5000, Utc(2026, 9, 1));

            var result = SpeedSeasonAdminCommands.SelectCurrentBoardRow(new List<CharacterSpeedRun> { row });

            Assert.AreEqual(row.Id, result.Id);
        }

        [TestMethod]
        public void SelectCurrentBoardRow_MultipleRows_PicksTheLowestCentiseconds()
        {
            var slow = MakeRow(1, 100, "Bob", 4, 9000, Utc(2026, 9, 1));
            var fast = MakeRow(2, 100, "Bob", 4, 3000, Utc(2026, 9, 2));
            var middle = MakeRow(3, 100, "Bob", 4, 6000, Utc(2026, 9, 3));

            var result = SpeedSeasonAdminCommands.SelectCurrentBoardRow(new List<CharacterSpeedRun> { slow, fast, middle });

            Assert.AreEqual(fast.Id, result.Id, "lower is better on the speed board");
        }

        [TestMethod]
        public void SelectCurrentBoardRow_ANonPositiveTime_NeverWinsOverARealTime()
        {
            var artefact = MakeRow(1, 100, "Bob", 4, 0, Utc(2026, 9, 1));
            var real = MakeRow(2, 100, "Bob", 4, 12345, Utc(2026, 9, 2));

            var result = SpeedSeasonAdminCommands.SelectCurrentBoardRow(new List<CharacterSpeedRun> { artefact, real });

            Assert.AreEqual(real.Id, result.Id, "must agree with SpeedBoardManager.CompareEntries, where a backwards-clock artefact always ranks last");
        }

        [TestMethod]
        public void SelectCurrentBoardRow_EqualTimes_EarlierCompletionWins()
        {
            var earlier = MakeRow(1, 100, "Bob", 4, 10000, Utc(2026, 9, 1));
            var later = MakeRow(2, 100, "Bob", 4, 10000, Utc(2026, 9, 10));

            var result = SpeedSeasonAdminCommands.SelectCurrentBoardRow(new List<CharacterSpeedRun> { later, earlier });

            Assert.AreEqual(earlier.Id, result.Id, "first to achieve the time holds the higher rank, mirroring SpeedBoardManager.CompareEntries");
        }

        [TestMethod]
        public void SelectCurrentBoardRow_EmptyList_ReturnsNull()
        {
            Assert.IsNull(SpeedSeasonAdminCommands.SelectCurrentBoardRow(new List<CharacterSpeedRun>()));
        }

        [TestMethod]
        public void SelectCurrentBoardRow_Null_ReturnsNullRatherThanThrowing()
        {
            Assert.IsNull(SpeedSeasonAdminCommands.SelectCurrentBoardRow(null));
        }

        // =======================================================================================
        // GroupMatchingRowsByCharacter - the board is keyed by CharacterId (SpeedBoardManager.cs), and
        // character_speed_run deliberately outlives a deleted/renamed character, so two CharacterIds can
        // legitimately share a CharacterName snapshot. Grouping by name alone would merge their rows and
        // hand out one "current board entry" marker for two different board lines - this is the
        // regression these tests exist for.
        // =======================================================================================

        [TestMethod]
        public void GroupMatchingRowsByCharacter_ASingleCharacter_ReturnsOneGroup_MatchingTodaysOutput()
        {
            // Rows are expected to already be scoped to one season by the caller (GetSpeedRunsBySeason),
            // so this helper only filters by name - hence no other-season row in this fixture.
            var slow = MakeRow(1, 100, "Bob", 4, 9000, Utc(2026, 9, 1));
            var fast = MakeRow(2, 100, "Bob", 4, 3000, Utc(2026, 9, 2));
            var otherName = MakeRow(3, 200, "Alice", 4, 1000, Utc(2026, 9, 3));

            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(new List<CharacterSpeedRun> { slow, fast, otherName }, "bob");

            Assert.AreEqual(1, groups.Count, "one CharacterId matching the name must produce exactly one group");
            Assert.AreEqual(100u, groups[0].CharacterId);
            CollectionAssert.AreEqual(new[] { slow.Id, fast.Id }, new List<uint> { groups[0].Rows[0].Id, groups[0].Rows[1].Id },
                "a row belonging to a different CharacterId (even matched by a different name) must not appear");
            Assert.AreEqual(fast.Id, groups[0].CurrentBoardRow.Id, "the current board entry must be the character's best time");
        }

        [TestMethod]
        public void GroupMatchingRowsByCharacter_NameMatchIsCaseInsensitive()
        {
            var row = MakeRow(1, 100, "Bob", 4, 9000, Utc(2026, 9, 1));

            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(new List<CharacterSpeedRun> { row }, "BOB");

            Assert.AreEqual(1, groups.Count);
        }

        [TestMethod]
        public void GroupMatchingRowsByCharacter_TwoCharacterIdsSharingAName_ProducesTwoGroups_EachWithItsOwnCurrentMarker()
        {
            // The exact scenario the fix covers: character_speed_run outlives a deleted/renamed
            // character, so a recreated character (or a different account) can hold the same name.
            var id1Slow = MakeRow(1, 100, "Bob", 4, 9000, Utc(2026, 9, 1));
            var id1Fast = MakeRow(2, 100, "Bob", 4, 3000, Utc(2026, 9, 2));
            var id2Slow = MakeRow(3, 200, "Bob", 4, 8000, Utc(2026, 9, 3));
            var id2Fast = MakeRow(4, 200, "Bob", 4, 2000, Utc(2026, 9, 4));

            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(
                new List<CharacterSpeedRun> { id1Slow, id1Fast, id2Slow, id2Fast }, "Bob");

            Assert.AreEqual(2, groups.Count, "two distinct CharacterIds sharing a name must produce two sections");
            Assert.AreEqual(100u, groups[0].CharacterId, "groups are ordered by CharacterId");
            Assert.AreEqual(200u, groups[1].CharacterId);

            Assert.AreEqual(2, groups[0].Rows.Count);
            Assert.AreEqual(2, groups[1].Rows.Count);

            Assert.AreEqual(id1Fast.Id, groups[0].CurrentBoardRow.Id, "CharacterId 100's current entry must be ITS best time, not the other character's");
            Assert.AreEqual(id2Fast.Id, groups[1].CurrentBoardRow.Id, "CharacterId 200's current entry must be ITS best time, not the other character's");
        }

        [TestMethod]
        public void GroupMatchingRowsByCharacter_TwoCharacterIdsSharingAName_RunIdsResolveToTheCorrectCharacter()
        {
            // Guards the confirm path's ownership resolution: a run id found among the flattened group
            // rows must carry the CharacterId of the row it actually belongs to, never the searched name.
            var id1Row = MakeRow(1, 100, "Bob", 4, 9000, Utc(2026, 9, 1));
            var id2Row = MakeRow(2, 200, "Bob", 4, 8000, Utc(2026, 9, 2));

            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(new List<CharacterSpeedRun> { id1Row, id2Row }, "Bob");

            var flattened = groups.SelectMany(g => g.Rows).ToList();

            var resolvedId1 = flattened.First(r => r.Id == 1);
            var resolvedId2 = flattened.First(r => r.Id == 2);

            Assert.AreEqual(100u, resolvedId1.CharacterId);
            Assert.AreEqual(200u, resolvedId2.CharacterId);
        }

        [TestMethod]
        public void GroupMatchingRowsByCharacter_NoMatches_ReturnsEmpty()
        {
            var row = MakeRow(1, 100, "Alice", 4, 9000, Utc(2026, 9, 1));

            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(new List<CharacterSpeedRun> { row }, "Bob");

            Assert.AreEqual(0, groups.Count);
        }

        [TestMethod]
        public void GroupMatchingRowsByCharacter_NullRows_ReturnsEmptyRatherThanThrowing()
        {
            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(null, "Bob");

            Assert.IsNotNull(groups);
            Assert.AreEqual(0, groups.Count);
        }

        [TestMethod]
        public void GroupMatchingRowsByCharacter_NullElements_AreSkipped()
        {
            var row = MakeRow(1, 100, "Bob", 4, 9000, Utc(2026, 9, 1));

            var groups = SpeedSeasonAdminCommands.GroupMatchingRowsByCharacter(new List<CharacterSpeedRun> { null, row, null }, "Bob");

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(1, groups[0].Rows.Count);
        }
    }
}
