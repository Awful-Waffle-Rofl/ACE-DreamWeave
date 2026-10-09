using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.World;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Proving Grounds: Speed - guards SpeedSeasonManager's pure selection helpers (season selection
    /// by clock, overlap detection, the additive merge, and the recent-seasons ordering). Runs with no
    /// database, no dat file and no world - everything here is exercised through the manager's public
    /// static pure functions over hand-built SpeedSeason rows.
    /// </summary>
    [TestClass]
    public class SpeedChallengeTests
    {
        private static SpeedSeason MakeSeason(int id, DateTime startsAt, DateTime endsAt, string name = null)
        {
            return new SpeedSeason
            {
                Id = id,
                Name = name ?? $"Season {id}",
                DungeonName = "Test Dungeon",
                ObjCellId = 0x0021B0FFu,
                OriginX = 90.0f,
                OriginY = 90.0f,
                OriginZ = 0.0f,
                AnglesW = 1.0f,
                AnglesX = 0.0f,
                AnglesY = 0.0f,
                AnglesZ = 0.0f,
                RealmId = 0,
                ObjectiveWcid = 1002999,
                LevelFloor = 50,
                StartsAt = startsAt,
                EndsAt = endsAt,
            };
        }

        private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        // =======================================================================================
        // SelectSeasonAt
        // =======================================================================================

        [TestMethod]
        public void SelectSeasonAt_TimeInsideASingleSeason_SelectsIt()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectSeasonAt(new[] { season }, Utc(2026, 9, 15));

            Assert.AreEqual(season, result, "a time strictly inside the season's window must select it");
        }

        [TestMethod]
        public void SelectSeasonAt_TimeInAGapBetweenSeasons_SelectsNothing()
        {
            var a = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 9, 10));
            var b = MakeSeason(2, Utc(2026, 9, 20), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectSeasonAt(new[] { a, b }, Utc(2026, 9, 15));

            Assert.IsNull(result, "a time in the gap between two seasons must select nothing");
        }

        [TestMethod]
        public void SelectSeasonAt_ExactlyStartsAt_SelectsTheSeason()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectSeasonAt(new[] { season }, Utc(2026, 9, 1));

            Assert.AreEqual(season, result, "StartsAt is inclusive");
        }

        [TestMethod]
        public void SelectSeasonAt_ExactlyEndsAt_DoesNotSelectTheSeason()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectSeasonAt(new[] { season }, Utc(2026, 10, 1));

            Assert.IsNull(result, "EndsAt is exclusive");
        }

        [TestMethod]
        public void SelectSeasonAt_AdjacentSeasons_HandOverAtTheExactInstant()
        {
            var handoff = Utc(2026, 10, 1);

            var a = MakeSeason(1, Utc(2026, 9, 1), handoff);
            var b = MakeSeason(2, handoff, Utc(2026, 11, 1));

            Assert.AreEqual(a, SpeedSeasonManager.SelectSeasonAt(new[] { a, b }, handoff.AddTicks(-1)), "one tick before the handoff, the outgoing season still owns the instant");
            Assert.AreEqual(b, SpeedSeasonManager.SelectSeasonAt(new[] { a, b }, handoff), "at the handoff instant, the incoming season owns it - no overlap, no gap");
        }

        [TestMethod]
        public void SelectSeasonAt_OverlappingRows_LatestStartsAtWinsRegardlessOfOrder()
        {
            var earlier = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            var later = MakeSeason(2, Utc(2026, 9, 10), Utc(2026, 10, 10));

            var forward = SpeedSeasonManager.SelectSeasonAt(new[] { earlier, later }, Utc(2026, 9, 20));
            var reversed = SpeedSeasonManager.SelectSeasonAt(new[] { later, earlier }, Utc(2026, 9, 20));

            Assert.AreEqual(later, forward, "the row with the later StartsAt must win");
            Assert.AreEqual(later, reversed, "the result must not depend on input order");
        }

        [TestMethod]
        public void SelectSeasonAt_OverlappingRowsTiedOnStartsAt_HigherIdWinsRegardlessOfOrder()
        {
            var start = Utc(2026, 9, 1);

            var lowId = MakeSeason(1, start, Utc(2026, 10, 1));
            var highId = MakeSeason(2, start, Utc(2026, 10, 5));

            var forward = SpeedSeasonManager.SelectSeasonAt(new[] { lowId, highId }, Utc(2026, 9, 15));
            var reversed = SpeedSeasonManager.SelectSeasonAt(new[] { highId, lowId }, Utc(2026, 9, 15));

            Assert.AreEqual(highId, forward, "on an exact StartsAt tie, the higher Id must win");
            Assert.AreEqual(highId, reversed, "the result must not depend on input order");
        }

        [TestMethod]
        public void SelectSeasonAt_MalformedRow_IsNeverSelected()
        {
            // EndsAt <= StartsAt: malformed, even though utcNow falls "inside" the range the two values span
            var malformed = MakeSeason(1, Utc(2026, 10, 1), Utc(2026, 9, 1));

            var result = SpeedSeasonManager.SelectSeasonAt(new[] { malformed }, Utc(2026, 9, 15));

            Assert.IsNull(result, "a malformed row (EndsAt <= StartsAt) must never be selectable");
        }

        [TestMethod]
        public void SelectSeasonAt_EmptyCandidates_ReturnsNullRatherThanThrowing()
        {
            Assert.IsNull(SpeedSeasonManager.SelectSeasonAt(new List<SpeedSeason>(), Utc(2026, 9, 15)));
        }

        [TestMethod]
        public void SelectSeasonAt_NullCandidates_ReturnsNullRatherThanThrowing()
        {
            Assert.IsNull(SpeedSeasonManager.SelectSeasonAt(null, Utc(2026, 9, 15)));
        }

        // =======================================================================================
        // FindOverlaps
        // =======================================================================================

        [TestMethod]
        public void FindOverlaps_OverlappingPair_IsFound()
        {
            var a = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            var b = MakeSeason(2, Utc(2026, 9, 15), Utc(2026, 10, 15));

            var overlaps = SpeedSeasonManager.FindOverlaps(new[] { a, b });

            Assert.AreEqual(1, overlaps.Count);
            Assert.AreEqual(a, overlaps[0].First);
            Assert.AreEqual(b, overlaps[0].Second);
        }

        [TestMethod]
        public void FindOverlaps_AdjacentTouchingWindows_ReturnsEmpty()
        {
            var handoff = Utc(2026, 10, 1);

            var a = MakeSeason(1, Utc(2026, 9, 1), handoff);
            var b = MakeSeason(2, handoff, Utc(2026, 11, 1));

            var overlaps = SpeedSeasonManager.FindOverlaps(new[] { a, b });

            Assert.AreEqual(0, overlaps.Count, "touching (half-open) windows must not count as an overlap");
        }

        [TestMethod]
        public void FindOverlaps_DisjointWindows_ReturnsEmpty()
        {
            var a = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 9, 10));
            var b = MakeSeason(2, Utc(2026, 9, 20), Utc(2026, 10, 1));

            var overlaps = SpeedSeasonManager.FindOverlaps(new[] { a, b });

            Assert.AreEqual(0, overlaps.Count);
        }

        // =======================================================================================
        // MergeSeasons
        // =======================================================================================

        [TestMethod]
        public void MergeSeasons_NewId_CountsAsAdded()
        {
            var target = new Dictionary<int, SpeedSeason>();
            var row = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var (added, updated, seen) = SpeedSeasonManager.MergeSeasons(target, new[] { row });

            Assert.AreEqual(1, added);
            Assert.AreEqual(0, updated);
            Assert.IsTrue(seen.Contains(1));
            Assert.AreSame(row, target[1]);
        }

        [TestMethod]
        public void MergeSeasons_ExistingId_CountsAsUpdatedAndReplacesValues()
        {
            var original = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1), "Original Name");
            var target = new Dictionary<int, SpeedSeason> { [1] = original };

            var replacement = MakeSeason(1, Utc(2026, 9, 5), Utc(2026, 10, 5), "New Name");

            var (added, updated, seen) = SpeedSeasonManager.MergeSeasons(target, new[] { replacement });

            Assert.AreEqual(0, added);
            Assert.AreEqual(1, updated);
            Assert.IsTrue(seen.Contains(1));
            Assert.AreSame(replacement, target[1], "the existing id's values must be replaced");
            Assert.AreEqual("New Name", target[1].Name);
        }

        [TestMethod]
        public void MergeSeasons_IdAbsentFromIncomingRows_IsReportedMissingButKept()
        {
            var kept = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            var target = new Dictionary<int, SpeedSeason> { [1] = kept };

            var incoming = MakeSeason(2, Utc(2026, 10, 1), Utc(2026, 11, 1));

            var (added, updated, seen) = SpeedSeasonManager.MergeSeasons(target, new[] { incoming });

            Assert.AreEqual(1, added);
            Assert.AreEqual(0, updated);
            Assert.IsFalse(seen.Contains(1), "id 1 was not in the incoming rows, so it must not be marked seen");
            Assert.IsTrue(target.ContainsKey(1), "id 1 must be kept in the dictionary, not removed");
            Assert.AreSame(kept, target[1]);
        }

        [TestMethod]
        public void MergeSeasons_MalformedRow_IsSkippedAndCountedAsNeither()
        {
            var target = new Dictionary<int, SpeedSeason>();
            var malformed = MakeSeason(1, Utc(2026, 10, 1), Utc(2026, 9, 1)); // EndsAt <= StartsAt

            var (added, updated, seen) = SpeedSeasonManager.MergeSeasons(target, new[] { malformed });

            Assert.AreEqual(0, added);
            Assert.AreEqual(0, updated);
            Assert.IsFalse(seen.Contains(1));
            Assert.IsFalse(target.ContainsKey(1));
        }

        // =======================================================================================
        // IsWellFormed
        // =======================================================================================

        [TestMethod]
        public void IsWellFormed_NullSeason_IsFalse()
        {
            Assert.IsFalse(SpeedSeasonManager.IsWellFormed(null));
        }

        [TestMethod]
        public void IsWellFormed_EndsAtNotAfterStartsAt_IsFalse()
        {
            Assert.IsFalse(SpeedSeasonManager.IsWellFormed(MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 9, 1))), "EndsAt == StartsAt is malformed");
            Assert.IsFalse(SpeedSeasonManager.IsWellFormed(MakeSeason(1, Utc(2026, 9, 5), Utc(2026, 9, 1))), "EndsAt < StartsAt is malformed");
        }

        [TestMethod]
        public void IsWellFormed_BlankName_IsFalse()
        {
            Assert.IsFalse(SpeedSeasonManager.IsWellFormed(MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1), name: "")));
            Assert.IsFalse(SpeedSeasonManager.IsWellFormed(MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1), name: "   ")));

            var trueNull = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            trueNull.Name = null;
            Assert.IsFalse(SpeedSeasonManager.IsWellFormed(trueNull), "a Name of null (not just the builder's default) must be malformed too");
        }

        [TestMethod]
        public void IsWellFormed_ValidSeason_IsTrue()
        {
            Assert.IsTrue(SpeedSeasonManager.IsWellFormed(MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1))));
        }

        // =======================================================================================
        // SelectRecentSeasons (the pure seam GetRecentSeasons(int) delegates to)
        // =======================================================================================

        [TestMethod]
        public void SelectRecentSeasons_OrdersByNewestStartsAtFirst()
        {
            var oldest = MakeSeason(1, Utc(2026, 7, 1), Utc(2026, 8, 1));
            var middle = MakeSeason(2, Utc(2026, 8, 1), Utc(2026, 9, 1));
            var newest = MakeSeason(3, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectRecentSeasons(new[] { oldest, newest, middle }, 10);

            CollectionAssert.AreEqual(new[] { newest, middle, oldest }, result.ToArray());
        }

        [TestMethod]
        public void SelectRecentSeasons_TieOnStartsAt_HigherIdFirst()
        {
            var start = Utc(2026, 9, 1);

            var lowId = MakeSeason(1, start, Utc(2026, 10, 1));
            var highId = MakeSeason(2, start, Utc(2026, 10, 5));

            var result = SpeedSeasonManager.SelectRecentSeasons(new[] { lowId, highId }, 10);

            CollectionAssert.AreEqual(new[] { highId, lowId }, result.ToArray());
        }

        [TestMethod]
        public void SelectRecentSeasons_HonoursCount()
        {
            var a = MakeSeason(1, Utc(2026, 7, 1), Utc(2026, 8, 1));
            var b = MakeSeason(2, Utc(2026, 8, 1), Utc(2026, 9, 1));
            var c = MakeSeason(3, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectRecentSeasons(new[] { a, b, c }, 2);

            Assert.AreEqual(2, result.Count);
            CollectionAssert.AreEqual(new[] { c, b }, result.ToArray());
        }

        [TestMethod]
        public void SelectRecentSeasons_IncludesTheCurrentlyActiveSeason()
        {
            var active = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            var past = MakeSeason(2, Utc(2026, 8, 1), Utc(2026, 9, 1));

            var result = SpeedSeasonManager.SelectRecentSeasons(new[] { past, active }, 10);

            Assert.IsTrue(result.Contains(active), "the currently-active season must be included");
        }

        [TestMethod]
        public void SelectRecentSeasons_CountZeroOrNegative_ReturnsEmpty()
        {
            var a = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            Assert.AreEqual(0, SpeedSeasonManager.SelectRecentSeasons(new[] { a }, 0).Count);
            Assert.AreEqual(0, SpeedSeasonManager.SelectRecentSeasons(new[] { a }, -1).Count);
        }

        [TestMethod]
        public void SelectRecentSeasons_FewerThanCountExist_ReturnsWhatExists()
        {
            var a = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            var result = SpeedSeasonManager.SelectRecentSeasons(new[] { a }, 20);

            Assert.AreEqual(1, result.Count);
        }

        // =======================================================================================
        // Player.ToSpeedRunCentiseconds - the run controller's elapsed-time conversion
        // (Player_SpeedChallenge.cs). Pure static, so it needs no world, session or database.
        // =======================================================================================

        [TestMethod]
        public void ToSpeedRunCentiseconds_WholeSecond_IsOneHundredCentiseconds()
        {
            Assert.AreEqual(100L, Player.ToSpeedRunCentiseconds(1.0));
            Assert.AreEqual(4500L, Player.ToSpeedRunCentiseconds(45.0));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_SubSecond_KeepsHundredthsResolution()
        {
            Assert.AreEqual(1L, Player.ToSpeedRunCentiseconds(0.01));
            Assert.AreEqual(50L, Player.ToSpeedRunCentiseconds(0.5));
            Assert.AreEqual(99L, Player.ToSpeedRunCentiseconds(0.99));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_FractionFinerThanACentisecond_IsFlooredNotRounded()
        {
            // 12.3456s is 1234.56 centiseconds: floored, so a time is never credited as faster than it was
            Assert.AreEqual(1234L, Player.ToSpeedRunCentiseconds(12.3456));
            Assert.AreEqual(1234L, Player.ToSpeedRunCentiseconds(12.3499));
            Assert.AreEqual(1235L, Player.ToSpeedRunCentiseconds(12.35));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_OverAMinute_ScalesLinearly()
        {
            Assert.AreEqual(6000L, Player.ToSpeedRunCentiseconds(60.0));
            Assert.AreEqual(12345L, Player.ToSpeedRunCentiseconds(123.45));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_NegativeElapsed_ClampsToZero()
        {
            // a wall clock stepping backwards mid-run must never record a negative (unbeatable) time
            Assert.AreEqual(0L, Player.ToSpeedRunCentiseconds(-0.01));
            Assert.AreEqual(0L, Player.ToSpeedRunCentiseconds(-1.0));
            Assert.AreEqual(0L, Player.ToSpeedRunCentiseconds(-100000.0));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_ZeroElapsed_IsZero()
        {
            Assert.AreEqual(0L, Player.ToSpeedRunCentiseconds(0.0));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_NaN_ClampsToZeroRatherThanProducingGarbage()
        {
            Assert.AreEqual(0L, Player.ToSpeedRunCentiseconds(double.NaN));
        }

        [TestMethod]
        public void ToSpeedRunCentiseconds_AbsurdlyLargeElapsed_SaturatesRatherThanWrappingNegative()
        {
            // an unchecked (long) cast of an out-of-range double is undefined and can land negative;
            // a clock stepping forward by an absurd amount must still yield a non-negative time
            Assert.IsTrue(Player.ToSpeedRunCentiseconds(double.MaxValue) > 0);
            Assert.IsTrue(Player.ToSpeedRunCentiseconds(1e30) > 0);
            Assert.AreEqual(long.MaxValue, Player.ToSpeedRunCentiseconds(double.PositiveInfinity));
        }

        // =======================================================================================
        // Player.FormatSpeedRunTime - the m:ss.cc render used by the run announcement and /top speed
        // =======================================================================================

        [TestMethod]
        public void FormatSpeedRunTime_SubSecond_PadsBothFields()
        {
            Assert.AreEqual("0:00.07", Player.FormatSpeedRunTime(7));
            Assert.AreEqual("0:00.99", Player.FormatSpeedRunTime(99));
        }

        [TestMethod]
        public void FormatSpeedRunTime_SubMinute_RendersSecondsAndHundredths()
        {
            Assert.AreEqual("0:01.00", Player.FormatSpeedRunTime(100));
            Assert.AreEqual("0:45.05", Player.FormatSpeedRunTime(4505));
            Assert.AreEqual("0:59.99", Player.FormatSpeedRunTime(5999));
        }

        [TestMethod]
        public void FormatSpeedRunTime_OverAMinute_RollsIntoMinutes()
        {
            Assert.AreEqual("1:00.00", Player.FormatSpeedRunTime(6000));
            Assert.AreEqual("2:03.45", Player.FormatSpeedRunTime(12345));
            Assert.AreEqual("10:00.01", Player.FormatSpeedRunTime(60001));
        }

        [TestMethod]
        public void FormatSpeedRunTime_ZeroAndNegative_RenderAsZero()
        {
            Assert.AreEqual("0:00.00", Player.FormatSpeedRunTime(0));
            Assert.AreEqual("0:00.00", Player.FormatSpeedRunTime(-1));
        }

        // Player.IsSeasonObjective - DESIGN section 7's central integrity rule: the SEASON ROW, not the bool
        // flag an object carries, decides what finishing means. The rest of TryFinishSpeedChallenge needs a
        // live Player, a Session and a landblock, so this gate is factored out precisely so the rule itself
        // can be guarded here.

        [TestMethod]
        public void IsSeasonObjective_TheSeasonsDeclaredWcid_Finishes()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            Assert.IsTrue(Player.IsSeasonObjective(season.ObjectiveWcid, season),
                "the wcid the season declares as its objective must finish the run");
        }

        [TestMethod]
        public void IsSeasonObjective_AnotherSeasonsFlaggedProp_DoesNotFinish()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));

            Assert.IsFalse(Player.IsSeasonObjective(season.ObjectiveWcid + 1, season),
                "a goal object or boss left flagged from another season must not end this season's run early");
        }

        [TestMethod]
        public void IsSeasonObjective_NullSeason_FailsClosed()
        {
            Assert.IsFalse(Player.IsSeasonObjective(1002999, null),
                "no season row means no declared objective, so nothing may finish a run");
        }

        [TestMethod]
        public void IsSeasonObjective_ZeroWcidAgainstAZeroObjective_StillComparesExactly()
        {
            // guards against a "0 means unset, so let anything through" reading creeping in later: an
            // objective_wcid of 0 is a content error, but it must still gate rather than open the door.
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            season.ObjectiveWcid = 0u;

            Assert.IsTrue(Player.IsSeasonObjective(0u, season), "the comparison is exact, not a truthiness test");
            Assert.IsFalse(Player.IsSeasonObjective(1002999, season), "a real wcid must not satisfy a zero objective");
        }

        // Player.IsSeasonStartObject - the clock-rebase counterpart to IsSeasonObjective above: does this
        // object rebase a run's clock to now? Mirrors that gate's shape and its reasons for being a pure
        // static, and additionally fails closed on an unset StartWcid, so a season that never opted into a
        // lever start cannot be started by a stray flagged object.

        [TestMethod]
        public void IsSeasonStartObject_TheSeasonsDeclaredStartWcid_Starts()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            season.StartWcid = 1002500;

            Assert.IsTrue(Player.IsSeasonStartObject(season.StartWcid, season),
                "the wcid the season declares as its start object must rebase the clock");
        }

        [TestMethod]
        public void IsSeasonStartObject_NullSeason_FailsClosed()
        {
            Assert.IsFalse(Player.IsSeasonStartObject(1002500, null),
                "no season row means no declared start object, so nothing may rebase the clock");
        }

        [TestMethod]
        public void IsSeasonStartObject_UnsetStartWcid_FailsClosed()
        {
            // a season that never opted into a lever start (StartWcid 0, the default) must not be startable
            // by a stray flagged object left over from a different season's dungeon.
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            season.StartWcid = 0;

            Assert.IsFalse(Player.IsSeasonStartObject(0u, season),
                "StartWcid 0 means arrival-start, not a wildcard - it must never match");
            Assert.IsFalse(Player.IsSeasonStartObject(1002500, season),
                "a real wcid must not satisfy an unset start object either");
        }

        [TestMethod]
        public void IsSeasonStartObject_AnotherSeasonsFlaggedProp_DoesNotStart()
        {
            var season = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            season.StartWcid = 1002500;

            Assert.IsFalse(Player.IsSeasonStartObject(season.StartWcid + 1, season),
                "a start object left flagged from another season must not rebase this season's clock");
        }

        // =======================================================================================
        // SpeedBoardManager.CompareEntries / BuildBoard
        //
        // The board is ONE line per character per season - that character's best (lowest) time - ranked
        // ASCENDING, because on this board lower is better. Both helpers are pure statics precisely so
        // the ranking rule is guarded here without a database, a live world or a Player.
        // =======================================================================================

        private static SpeedBoardEntry MakeEntry(uint characterId, long centiseconds, DateTime completedAt, int seasonId = 1, string name = null, int level = 100)
        {
            return new SpeedBoardEntry(characterId, name ?? $"Runner {characterId}", seasonId, centiseconds, level, completedAt);
        }

        [TestMethod]
        public void BuildBoard_RanksAscendingByCentiseconds()
        {
            var board = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(3, 30000, Utc(2026, 9, 3)),
                MakeEntry(1, 10000, Utc(2026, 9, 1)),
                MakeEntry(2, 20000, Utc(2026, 9, 2)),
            });

            CollectionAssert.AreEqual(new[] { 10000L, 20000L, 30000L }, board.Select(e => e.Centiseconds).ToArray(),
                "lower is better on the speed board, so it ranks ascending");
        }

        [TestMethod]
        public void BuildBoard_SeveralRunsForOneCharacter_CollapseToThatCharactersBest()
        {
            var board = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(1, 30000, Utc(2026, 9, 1)),
                MakeEntry(1, 12345, Utc(2026, 9, 2)),
                MakeEntry(1, 20000, Utc(2026, 9, 3)),
                MakeEntry(2, 25000, Utc(2026, 9, 4)),
            });

            Assert.AreEqual(2, board.Count, "a player who runs many times occupies ONE line, not one per run");
            Assert.AreEqual(1u, board[0].CharacterId);
            Assert.AreEqual(12345L, board[0].Centiseconds, "the surviving line is that character's fastest run");
            Assert.AreEqual(2u, board[1].CharacterId);
        }

        [TestMethod]
        public void BuildBoard_EqualTimes_TheEarlierCompletionRanksHigher()
        {
            var board = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(7, 10000, Utc(2026, 9, 10)),
                MakeEntry(4, 10000, Utc(2026, 9, 2)),
            });

            Assert.AreEqual(4u, board[0].CharacterId, "first to achieve the time holds the higher rank");
            Assert.AreEqual(7u, board[1].CharacterId);
        }

        [TestMethod]
        public void BuildBoard_EqualTimesAndEqualInstants_TieBreakOnCharacterId()
        {
            var when = Utc(2026, 9, 5, 12, 30, 0);

            var board = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(9, 10000, when),
                MakeEntry(2, 10000, when),
                MakeEntry(5, 10000, when),
            });

            CollectionAssert.AreEqual(new[] { 2u, 5u, 9u }, board.Select(e => e.CharacterId).ToArray(),
                "the final tie-break is ascending CharacterId, so ordering never depends on input order");
        }

        [TestMethod]
        public void BuildBoard_IsIndependentOfInputOrder()
        {
            var when = Utc(2026, 9, 5, 12, 30, 0);

            var forwards = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(2, 10000, when),
                MakeEntry(5, 10000, when),
                MakeEntry(9, 10000, when),
            });

            var backwards = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(9, 10000, when),
                MakeEntry(5, 10000, when),
                MakeEntry(2, 10000, when),
            });

            CollectionAssert.AreEqual(forwards.Select(e => e.CharacterId).ToArray(), backwards.Select(e => e.CharacterId).ToArray(),
                "CompareEntries is a total order, so the same rows rank the same way whatever order they arrive in");
        }

        [TestMethod]
        public void BuildBoard_NullSequence_ReturnsAnEmptyBoard()
        {
            var board = SpeedBoardManager.BuildBoard(null);

            Assert.IsNotNull(board);
            Assert.AreEqual(0, board.Count);
        }

        [TestMethod]
        public void BuildBoard_NullElements_AreSkipped()
        {
            var board = SpeedBoardManager.BuildBoard(new[]
            {
                null,
                MakeEntry(1, 10000, Utc(2026, 9, 1)),
                null,
                MakeEntry(2, 20000, Utc(2026, 9, 2)),
            });

            Assert.AreEqual(2, board.Count);
            Assert.IsFalse(board.Any(e => e == null));
        }

        [TestMethod]
        public void BuildBoard_ANonPositiveTime_StillAppearsOnTheBoard_ButRanksLast()
        {
            // The cache must AGREE with character_speed_run, which is the record of truth, so a
            // backwards-clock artefact is cached like any other row. It is excluded from the RECORD BAR
            // (GetSeasonRecord) rather than from the board - but it ranks BELOW every real time rather
            // than at the top, where its raw value would otherwise put it.
            var board = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(1, 10000, Utc(2026, 9, 2)),
                MakeEntry(2, 0, Utc(2026, 9, 1)),
            });

            Assert.AreEqual(2, board.Count, "a zero time is still a row in the table, so it is still a line in the cache");
            Assert.AreEqual(1u, board[0].CharacterId, "a real time outranks an artefact whose raw value is lower");
            Assert.AreEqual(2u, board[1].CharacterId);
        }

        [TestMethod]
        public void CompareEntries_ANonPositiveTime_RanksBelowEveryRealTime()
        {
            var artefact = MakeEntry(1, 0, Utc(2026, 9, 1));
            var slowest = MakeEntry(2, long.MaxValue, Utc(2026, 9, 2));

            Assert.IsTrue(SpeedBoardManager.CompareEntries(slowest, artefact) < 0,
                "even the slowest real run outranks a backwards-clock artefact");
            Assert.IsTrue(SpeedBoardManager.CompareEntries(artefact, slowest) > 0);
        }

        [TestMethod]
        public void CompareEntries_TwoNonPositiveTimes_StillHaveATotalOrder()
        {
            // both are artefacts, so neither has a real time to rank on; the tie-breaks still have to
            // produce a stable order or the board could shuffle between rebuilds
            var earlier = MakeEntry(1, 0, Utc(2026, 9, 1));
            var later = MakeEntry(2, 0, Utc(2026, 9, 2));

            Assert.IsTrue(SpeedBoardManager.CompareEntries(earlier, later) < 0);
            Assert.IsTrue(SpeedBoardManager.CompareEntries(later, earlier) > 0);
        }

        [TestMethod]
        public void BuildBoard_SameCharacterArtefactThenRealTime_KeepsTheRealTime()
        {
            // The regression this rule exists for. A raw ascending compare made the 0 that character's
            // permanent best, and because GetSeasonLeader and the /top speed renderer both skip
            // non-positive entries, the character then vanished from the board for the whole season -
            // and character_speed_run being append-only meant a restart rebuilt the same collapse.
            var board = SpeedBoardManager.BuildBoard(new[]
            {
                MakeEntry(1, 0, Utc(2026, 9, 1)),
                MakeEntry(1, 12345, Utc(2026, 9, 2)),
            });

            Assert.AreEqual(1, board.Count, "one line per character");
            Assert.AreEqual(12345, board[0].Centiseconds, "the genuine run replaces the artefact");
        }

        // =======================================================================================
        // SpeedBoardManager.RecordCompletion - the mutation path the finish funnel actually calls.
        // The BuildBoard tests above cover the same collapse rule, but they exercise the pure rebuild
        // helper rather than this method, so a divergence between the two would go unnoticed. Each test
        // uses its OWN season id: `boards` is static and lives for the whole test run, so a shared id
        // would let one test see another's rows.
        // =======================================================================================

        [TestMethod]
        public void RecordCompletion_FirstRun_IsInserted()
        {
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 10000, Utc(2026, 9, 1), seasonId: 90001));

            var board = SpeedBoardManager.GetSeasonBoard(90001);

            Assert.AreEqual(1, board.Count);
            Assert.AreEqual(10000, board[0].Centiseconds);
        }

        [TestMethod]
        public void RecordCompletion_ABetterTime_ReplacesTheCharactersLine()
        {
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 10000, Utc(2026, 9, 1), seasonId: 90002));
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 9000, Utc(2026, 9, 2), seasonId: 90002));

            var board = SpeedBoardManager.GetSeasonBoard(90002);

            Assert.AreEqual(1, board.Count, "one line per character, not one per run");
            Assert.AreEqual(9000, board[0].Centiseconds);
        }

        [TestMethod]
        public void RecordCompletion_AWorseTime_LeavesTheCharactersLineAlone()
        {
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 9000, Utc(2026, 9, 1), seasonId: 90003));
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 15000, Utc(2026, 9, 2), seasonId: 90003));

            var board = SpeedBoardManager.GetSeasonBoard(90003);

            Assert.AreEqual(1, board.Count);
            Assert.AreEqual(9000, board[0].Centiseconds, "a slower run never demotes a standing best");
        }

        [TestMethod]
        public void RecordCompletion_AnArtefactThenARealTime_KeepsTheRealTime()
        {
            // The live-path half of BuildBoard_SameCharacterArtefactThenRealTime_KeepsTheRealTime: the
            // artefact must not become a permanent best on the running server either.
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 0, Utc(2026, 9, 1), seasonId: 90004));
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 12345, Utc(2026, 9, 2), seasonId: 90004));

            var board = SpeedBoardManager.GetSeasonBoard(90004);

            Assert.AreEqual(1, board.Count);
            Assert.AreEqual(12345, board[0].Centiseconds);
        }

        [TestMethod]
        public void RecordCompletion_ARealTimeThenAnArtefact_KeepsTheRealTime()
        {
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 12345, Utc(2026, 9, 1), seasonId: 90005));
            SpeedBoardManager.RecordCompletion(MakeEntry(1, 0, Utc(2026, 9, 2), seasonId: 90005));

            var board = SpeedBoardManager.GetSeasonBoard(90005);

            Assert.AreEqual(1, board.Count);
            Assert.AreEqual(12345, board[0].Centiseconds, "an artefact never displaces a standing real time");
        }

        [TestMethod]
        public void RecordCompletion_Null_IsIgnored()
        {
            SpeedBoardManager.RecordCompletion(null);

            Assert.AreEqual(0, SpeedBoardManager.GetSeasonBoard(90006).Count);
        }

        [TestMethod]
        public void GetSeasonBoard_UnknownSeason_IsEmptyNotNull()
        {
            var board = SpeedBoardManager.GetSeasonBoard(90007);

            Assert.IsNotNull(board);
            Assert.AreEqual(0, board.Count);
        }

        [TestMethod]
        public void CompareEntries_Nulls_SortLast()
        {
            var entry = MakeEntry(1, 10000, Utc(2026, 9, 1));

            Assert.IsTrue(SpeedBoardManager.CompareEntries(entry, null) < 0);
            Assert.IsTrue(SpeedBoardManager.CompareEntries(null, entry) > 0);
            Assert.AreEqual(0, SpeedBoardManager.CompareEntries(null, null));
        }

        // =======================================================================================
        // Player.GetCachedSpeedBest - DESIGN 4.3's staleness rule for the personal-best biota cache.
        // A cached best whose season id does not match the season being scored reads as UNSET; that is
        // the entire reason the season id is stored beside the number.
        // =======================================================================================

        [TestMethod]
        public void GetCachedSpeedBest_MatchingSeason_ReturnsTheCachedValue()
        {
            Assert.AreEqual(12345L, Player.GetCachedSpeedBest(4, 12345L, 4));
        }

        [TestMethod]
        public void GetCachedSpeedBest_ADifferentSeason_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetCachedSpeedBest(3, 12345L, 4),
                "last season's best must not be compared against this season's run");
        }

        [TestMethod]
        public void GetCachedSpeedBest_NoStoredSeasonId_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetCachedSpeedBest(null, 12345L, 4),
                "a best with no season stamp is indistinguishable from a stale one, so it cannot be trusted");
        }

        [TestMethod]
        public void GetCachedSpeedBest_NoStoredBest_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetCachedSpeedBest(4, null, 4));
        }

        [TestMethod]
        public void GetCachedSpeedBest_ZeroOrNegativeBest_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetCachedSpeedBest(4, 0L, 4), "a zero best is a backwards-clock artefact, not a time to beat");
            Assert.IsNull(Player.GetCachedSpeedBest(4, -1L, 4));
        }

        // =======================================================================================
        // Player.ShouldResetSpeedRewardClaims - the season rollover for the reward NPC's claim-once
        // quest flags. Keyed on the SEASON ID alone, deliberately not on "the cached best is unset":
        // a same-season player with no best yet must NOT re-open the ladder, or all three reward
        // tiers become farmable within one season.
        // =======================================================================================

        [TestMethod]
        public void ShouldResetSpeedRewardClaims_NoStoredSeasonId_Resets()
        {
            Assert.IsTrue(Player.ShouldResetSpeedRewardClaims(null, 4),
                "an unstamped season id cannot be shown to be this season, so the claims are treated as stale");
        }

        [TestMethod]
        public void ShouldResetSpeedRewardClaims_ADifferentSeason_Resets()
        {
            Assert.IsTrue(Player.ShouldResetSpeedRewardClaims(3, 4),
                "a new season must re-open the reward ladder");
        }

        [TestMethod]
        public void ShouldResetSpeedRewardClaims_TheSameSeason_DoesNotReset()
        {
            Assert.IsFalse(Player.ShouldResetSpeedRewardClaims(4, 4),
                "re-opening the ladder mid-season would let every tier be claimed again");
        }

        // =======================================================================================
        // Player.ShouldClearStaleSpeedBest - the login-time clear in WorldManager.DoPlayerEnterWorld.
        // Same shape as the rollover reset above, but it DESTROYS the cached pair, so a null active
        // season (a scheduling gap) must clear nothing: a gap is not evidence the stamped season has
        // been superseded. Contrast GetSeasonAwareSpeedBest below, which fails the other way for the
        // opposite reason.
        // =======================================================================================

        [TestMethod]
        public void ShouldClearStaleSpeedBest_ADifferentActiveSeason_Clears()
        {
            Assert.IsTrue(Player.ShouldClearStaleSpeedBest(3, 4),
                "a best stamped with a season that is over must not survive into the next one");
        }

        [TestMethod]
        public void ShouldClearStaleSpeedBest_TheSameSeason_DoesNotClear()
        {
            Assert.IsFalse(Player.ShouldClearStaleSpeedBest(4, 4),
                "logging in during your own season must not wipe the time you already set in it");
        }

        [TestMethod]
        public void ShouldClearStaleSpeedBest_NoActiveSeason_ClearsNothing()
        {
            Assert.IsFalse(Player.ShouldClearStaleSpeedBest(3, null),
                "a scheduling gap is not a season change, and this branch destroys the number");
        }

        [TestMethod]
        public void ShouldClearStaleSpeedBest_NoStoredSeasonId_ClearsNothing()
        {
            Assert.IsFalse(Player.ShouldClearStaleSpeedBest(null, 4),
                "there is no stamped pair to clear; the emote gate already refuses to read an unstamped best");
        }

        // =======================================================================================
        // Player.GetSeasonAwareSpeedBest - what the reward NPC's InqInt64Stat actually reads. Fails
        // CLOSED on a scheduling gap, deliberately the opposite of ShouldClearStaleSpeedBest above:
        // that one destroys data so silence means leave it alone, this one authorizes a payout so
        // silence means refuse. Null rather than 0 is what routes the emote to TestNoQuality.
        // =======================================================================================

        [TestMethod]
        public void GetSeasonAwareSpeedBest_ThisSeasonsTime_IsReadable()
        {
            Assert.AreEqual(12345L, Player.GetSeasonAwareSpeedBest(4, 12345L, 4));
        }

        [TestMethod]
        public void GetSeasonAwareSpeedBest_LastSeasonsTime_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetSeasonAwareSpeedBest(3, 12345L, 4),
                "the online-across-rollover player must not be paid off a time from the season that just ended");
        }

        [TestMethod]
        public void GetSeasonAwareSpeedBest_NoActiveSeason_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetSeasonAwareSpeedBest(4, 12345L, null),
                "no season is running, so no season's ladder is open to pay from");
        }

        [TestMethod]
        public void GetSeasonAwareSpeedBest_NoStoredSeasonId_ReadsAsUnset()
        {
            Assert.IsNull(Player.GetSeasonAwareSpeedBest(null, 12345L, 4),
                "an unstamped best cannot be shown to belong to this season");
        }

        [TestMethod]
        public void GetSeasonAwareSpeedBest_NullNotZero_SoTheEmoteReachesTestNoQuality()
        {
            // InqInt64Stat routes a null stat to TestNoQuality BEFORE its `stat ??= 0` coercion. Coercing
            // to 0 here instead would slip under every tier's max_64 ceiling, which is the exact hole
            // weenie 1003002's min_64 = 1 exists to cover. Both guards, belt and braces.
            Assert.IsNull(Player.GetSeasonAwareSpeedBest(3, 12345L, 4));
            Assert.AreNotEqual(0L, Player.GetSeasonAwareSpeedBest(3, 12345L, 4));
        }

        // =======================================================================================
        // SelectNextSeasonAt - what /speedseason reports as the next scheduled rotation. Strictly
        // future: StartsAt is inclusive in SelectSeasonAt's half-open window, so a row that has
        // already started is either the active season or an ended one, never "next".
        // =======================================================================================

        [TestMethod]
        public void SelectNextSeasonAt_SeveralFutureSeasons_PicksTheEarliest()
        {
            var later = MakeSeason(1, Utc(2026, 11, 1), Utc(2026, 12, 1));
            var sooner = MakeSeason(2, Utc(2026, 10, 1), Utc(2026, 11, 1));

            var result = SpeedSeasonManager.SelectNextSeasonAt(new[] { later, sooner }, Utc(2026, 9, 15));

            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Id, "the next rotation is the soonest one, regardless of input order");
        }

        [TestMethod]
        public void SelectNextSeasonAt_TheActiveSeason_IsNotNext()
        {
            var active = MakeSeason(1, Utc(2026, 9, 1), Utc(2026, 10, 1));
            var upcoming = MakeSeason(2, Utc(2026, 10, 1), Utc(2026, 11, 1));

            var result = SpeedSeasonManager.SelectNextSeasonAt(new[] { active, upcoming }, Utc(2026, 9, 15));

            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Id, "a season already under way is the ACTIVE one, never the next one");
        }

        [TestMethod]
        public void SelectNextSeasonAt_AnEndedSeason_IsNotNext()
        {
            var ended = MakeSeason(1, Utc(2026, 7, 1), Utc(2026, 8, 1));

            var result = SpeedSeasonManager.SelectNextSeasonAt(new[] { ended }, Utc(2026, 9, 15));

            Assert.IsNull(result, "a season in the past is not scheduled");
        }

        [TestMethod]
        public void SelectNextSeasonAt_NothingScheduled_ReturnsNull()
        {
            var ended = MakeSeason(1, Utc(2026, 7, 1), Utc(2026, 8, 1));
            var active = MakeSeason(2, Utc(2026, 9, 1), Utc(2026, 10, 1));

            Assert.IsNull(SpeedSeasonManager.SelectNextSeasonAt(new[] { ended, active }, Utc(2026, 9, 15)));
            Assert.IsNull(SpeedSeasonManager.SelectNextSeasonAt(new SpeedSeason[0], Utc(2026, 9, 15)));
        }

        [TestMethod]
        public void SelectNextSeasonAt_NullCandidates_ReturnsNull()
        {
            Assert.IsNull(SpeedSeasonManager.SelectNextSeasonAt(null, Utc(2026, 9, 15)));
        }

        [TestMethod]
        public void SelectNextSeasonAt_TieOnStartsAt_PicksTheLowerId()
        {
            var higher = MakeSeason(7, Utc(2026, 10, 1), Utc(2026, 11, 1));
            var lower = MakeSeason(3, Utc(2026, 10, 1), Utc(2026, 11, 1));

            var result = SpeedSeasonManager.SelectNextSeasonAt(new[] { higher, lower }, Utc(2026, 9, 15));

            Assert.IsNotNull(result);
            Assert.AreEqual(3, result.Id, "selection must be deterministic, not dependent on input order");
        }

        [TestMethod]
        public void SelectNextSeasonAt_MalformedRows_AreSkipped()
        {
            var inverted = MakeSeason(1, Utc(2026, 10, 1), Utc(2026, 9, 1));
            var unnamed = MakeSeason(2, Utc(2026, 10, 5), Utc(2026, 11, 1), name: "   ");
            var good = MakeSeason(3, Utc(2026, 10, 10), Utc(2026, 11, 1));

            var result = SpeedSeasonManager.SelectNextSeasonAt(new[] { inverted, unnamed, good }, Utc(2026, 9, 15));

            Assert.IsNotNull(result);
            Assert.AreEqual(3, result.Id, "a malformed row is never selectable, so a later well-formed one wins");
        }

        // =======================================================================================
        // Player.FormatSpeedRunTime - the m:ss.cc render used by /top speed, /top speed winners and
        // the completion announcement. DESIGN section 8 asks for sub-second, sub-minute and
        // over-a-minute coverage explicitly.
        // =======================================================================================

        [TestMethod]
        public void FormatSpeedRunTime_SubSecond()
        {
            Assert.AreEqual("0:00.01", Player.FormatSpeedRunTime(1));
            Assert.AreEqual("0:00.50", Player.FormatSpeedRunTime(50));
            Assert.AreEqual("0:00.99", Player.FormatSpeedRunTime(99));
        }

        [TestMethod]
        public void FormatSpeedRunTime_SubMinute()
        {
            Assert.AreEqual("0:01.00", Player.FormatSpeedRunTime(100));
            Assert.AreEqual("0:42.07", Player.FormatSpeedRunTime(4207));
            Assert.AreEqual("0:59.99", Player.FormatSpeedRunTime(5999));
        }

        [TestMethod]
        public void FormatSpeedRunTime_OverAMinute()
        {
            Assert.AreEqual("1:00.00", Player.FormatSpeedRunTime(6000));
            Assert.AreEqual("1:35.05", Player.FormatSpeedRunTime(9505));
            Assert.AreEqual("12:34.56", Player.FormatSpeedRunTime(75456));
        }

        [TestMethod]
        public void FormatSpeedRunTime_PadsSecondsAndHundredthsToTwoDigits()
        {
            // the padding is the whole point of the format: an unpadded render would turn 1:05.09
            // into 1:5.9, which sorts and reads as a completely different time
            Assert.AreEqual("1:05.09", Player.FormatSpeedRunTime(6509));
            Assert.AreEqual("0:00.00", Player.FormatSpeedRunTime(0));
            Assert.AreEqual("125:00.00", Player.FormatSpeedRunTime(750000), "minutes are never truncated to an hours field");
        }

        [TestMethod]
        public void FormatSpeedRunTime_NegativeClampsToZero()
        {
            Assert.AreEqual("0:00.00", Player.FormatSpeedRunTime(-1),
                "a backwards-clock artefact renders as zero rather than as a negative time");
        }
    }
}
