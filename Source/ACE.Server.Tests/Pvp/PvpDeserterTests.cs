using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The pure deserter pieces (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"): the ladder, the exit table, the scope, the refusal phrase,
    /// and the record dispatcher's retry path. No PropertyManager reads: every dial is passed in.
    /// </summary>
    [TestClass]
    public class PvpDeserterTests
    {
        private const long Day = 86400;

        private const long Now = 1_800_000_000;

        /// <summary>Distinct lengths so a test can tell which rung was used; decay 7 days, as shipped.</summary>
        private static readonly PvpConductDials Dials = PvpConductTunables.Defaults with
        {
            DeserterEnabled = true,
            DeserterLockout1Minutes = 11,
            DeserterLockout2Minutes = 22,
            DeserterLockout3Minutes = 33,
            DeserterDecayDays = 7,
        };

        // ================= ladder =================

        [DataTestMethod]
        [DataRow(0, 1, 11)]
        [DataRow(1, 2, 22)]
        [DataRow(2, 3, 33)]
        [DataRow(3, 3, 33)]
        public void Next_AddsOneStrike_CapsAtThree_AndUsesThatRungsLength(int stored, int expectedStrikes, int expectedMinutes)
        {
            var r = PvpDeserterLadder.Next(stored, Now - 60, Now, Dials);

            Assert.AreEqual(new PvpDeserterRecord(expectedStrikes, Now, Now + expectedMinutes * 60L), r);
        }

        /// <summary>One strike drops per FULL clean decay period: a second short of 7 days decays nothing, exactly 7 days decays one.</summary>
        [DataTestMethod]
        [DataRow(7 * Day - 1, 3)]
        [DataRow(7 * Day, 2)]
        [DataRow(14 * Day - 1, 2)]
        [DataRow(14 * Day, 1)]
        [DataRow(100 * Day, 1)]
        public void Next_DecaysOneStrikePerFullDecayPeriod_BeforeAddingTheNewOne(long sinceLast, int expectedStrikes)
        {
            var r = PvpDeserterLadder.Next(2, Now - sinceLast, Now, Dials);

            Assert.AreEqual(expectedStrikes, r.Strikes);
            Assert.AreEqual(PvpDeserterLadder.LockoutMinutes(expectedStrikes, Dials) * 60, r.LockoutUntil - r.LastStrikeAt);
        }

        [DataTestMethod]
        [DataRow(3, 0L, 3)]
        [DataRow(3, 7 * Day, 2)]
        [DataRow(3, 21 * Day, 0)]
        [DataRow(1, 30 * Day, 0)]
        [DataRow(0, 0L, 0)]
        public void Decayed_StepsDownByFullPeriods_NeverBelowZero(int strikes, long sinceLast, int expected)
        {
            Assert.AreEqual(expected, PvpDeserterLadder.Decayed(strikes, Now - sinceLast, Now, 7));
        }

        [TestMethod]
        public void Next_WithTheSwitchOff_IsNull()
        {
            Assert.IsNull(PvpDeserterLadder.Next(2, Now - 60, Now, Dials with { DeserterEnabled = false }));
            Assert.AreEqual(0, PvpDeserterLadder.SecondsRemaining(new PvpDeserterRecord(1, Now, Now + 600), Now, Dials with { DeserterEnabled = false }), "a stored lockout does not hold while the switch is off");
            Assert.AreEqual(600, PvpDeserterLadder.SecondsRemaining(new PvpDeserterRecord(1, Now, Now + 600), Now, Dials), "control: it holds while on");
        }

        /// <summary>A clock that went backwards (now before the last strike) decays nothing and never throws; the new record is stamped at now.</summary>
        [TestMethod]
        public void Next_ClockWentBackwards_DecaysNothing()
        {
            var r = PvpDeserterLadder.Next(2, Now + 30 * Day, Now, Dials);

            Assert.AreEqual(new PvpDeserterRecord(3, Now, Now + 33 * 60), r);
            Assert.AreEqual(2, PvpDeserterLadder.Decayed(2, Now + 30 * Day, Now, 7));
        }

        /// <summary>Decay 0 days: a strike never outlives the moment, so every strike is the first.</summary>
        [TestMethod]
        public void Next_DecayZero_EveryStrikeIsTheFirst()
        {
            Assert.AreEqual(1, PvpDeserterLadder.Next(3, Now, Now, Dials with { DeserterDecayDays = 0 }).Strikes);
        }

        /// <summary>A stored count out of range is clamped: 9 counts as 3 (stays capped), -4 as 0.</summary>
        [TestMethod]
        public void Next_ClampsAStoredCountOutOfRange()
        {
            Assert.AreEqual(3, PvpDeserterLadder.Next(9, Now, Now, Dials).Strikes);
            Assert.AreEqual(1, PvpDeserterLadder.Next(-4, Now, Now, Dials).Strikes);
        }

        // ================= exit table =================

        /// <summary>THE EXIT TABLE, pinned value by value. A new PvpDeparture fails here until it is added to this table.</summary>
        [TestMethod]
        public void Classify_TheExitTable_IsExhaustive()
        {
            var expected = new Dictionary<PvpDeparture, PvpDeserterVerdict>
            {
                [PvpDeparture.Died] = PvpDeserterVerdict.NoStrike,
                [PvpDeparture.LeaveCommand] = PvpDeserterVerdict.Strike,
                [PvpDeparture.Logout] = PvpDeserterVerdict.Strike,
                [PvpDeparture.WalkedOut] = PvpDeserterVerdict.Strike,
                [PvpDeparture.IdleRemoval] = PvpDeserterVerdict.Strike,
                [PvpDeparture.FeedingRemoval] = PvpDeserterVerdict.Strike,
                [PvpDeparture.OfflineAtPlacement] = PvpDeserterVerdict.Strike,
                [PvpDeparture.TemplateBackstopEject] = PvpDeserterVerdict.NoStrike,
                [PvpDeparture.ServerShutdown] = PvpDeserterVerdict.NoStrike,
                [PvpDeparture.AdminCancel] = PvpDeserterVerdict.NoStrike,
                [PvpDeparture.CanceledBeforeLive] = PvpDeserterVerdict.NoStrike,
                [PvpDeparture.SpaceFailure] = PvpDeserterVerdict.NoStrike,
                [PvpDeparture.StaffOrServerLogoff] = PvpDeserterVerdict.NoStrike,
            };

            var all = Enum.GetValues(typeof(PvpDeparture)).Cast<PvpDeparture>().ToList();

            CollectionAssert.AreEquivalent(all, expected.Keys.ToList(), "every departure has a row in this table");

            foreach (var d in all)
                Assert.AreEqual(expected[d], PvpDeserterRules.Classify(d), d.ToString());

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PvpDeserterRules.Classify((PvpDeparture)999), "an unlisted value is refused, not defaulted");
        }

        /// <summary>Every ParticipantExit maps to a departure (a new exit fails here and in DepartureOf until mapped).</summary>
        [TestMethod]
        public void DepartureOf_MapsEveryParticipantExit()
        {
            var expected = new Dictionary<ParticipantExit, PvpDeparture>
            {
                [ParticipantExit.Died] = PvpDeparture.Died,
                [ParticipantExit.ForfeitLogout] = PvpDeparture.Logout,
                [ParticipantExit.ForfeitLeft] = PvpDeparture.WalkedOut,
                [ParticipantExit.ForfeitCommand] = PvpDeparture.LeaveCommand,
                [ParticipantExit.ForfeitInactive] = PvpDeparture.IdleRemoval,
                [ParticipantExit.ForfeitFeeding] = PvpDeparture.FeedingRemoval,
            };

            var all = Enum.GetValues(typeof(ParticipantExit)).Cast<ParticipantExit>().ToList();

            CollectionAssert.AreEquivalent(all, expected.Keys.ToList());

            foreach (var e in all)
                Assert.AreEqual(expected[e], PvpDeserterRules.DepartureOf(e), e.ToString());
        }

        [DataTestMethod]
        [DataRow("bg", true)]
        [DataRow("BG", true)]
        [DataRow("ffa", true)]
        [DataRow("1v1", false)]
        [DataRow("2v2", false)]
        [DataRow(null, false)]
        public void InScope_BattlegroundsAndTugakOnly(string roomKey, bool expected)
        {
            Assert.AreEqual(expected, PvpDeserterRules.InScope(roomKey));
        }

        // ================= text =================

        [DataTestMethod]
        [DataRow(1L, "1 second")]
        [DataRow(59L, "59 seconds")]
        [DataRow(60L, "1 minute")]
        [DataRow(61L, "2 minutes")]
        [DataRow(899L, "15 minutes")]
        [DataRow(900L, "15 minutes")]
        [DataRow(3599L, "1 hour")]
        [DataRow(5341L, "1 hour 30 minutes")]
        [DataRow(86400L, "24 hours")]
        public void DeserterRemainingPhrase_RoundsUpToWholeMinutesFromAMinute(long seconds, string expected)
        {
            Assert.AreEqual(expected, PvpArenaText.DeserterRemainingPhrase(seconds));
        }

        [DataTestMethod]
        [DataRow(1L, "1 second")]
        [DataRow(45L, "45 seconds")]
        [DataRow(60L, "1 minute")]
        [DataRow(900L, "15 minutes")]
        [DataRow(3540L, "59 minutes")]
        [DataRow(3600L, "1 hour")]
        [DataRow(3660L, "1 hour 1 minute")]
        [DataRow(5400L, "1 hour 30 minutes")]
        [DataRow(7200L, "2 hours")]
        [DataRow(86400L, "24 hours")]
        [DataRow(90000L, "25 hours")]
        public void LockoutDurationPhrase_HoursFromSixtyMinutes(long seconds, string expected)
        {
            Assert.AreEqual(expected, PvpArenaText.LockoutDurationPhrase(seconds));
        }

        /// <summary>The overtime announcement's DurationPhrase is NOT changed by the hours phrasing: 3600 s still reads in minutes there.</summary>
        [TestMethod]
        public void DurationPhrase_ForOvertime_IsUnchanged()
        {
            Assert.AreEqual("60 minutes", PvpArenaText.DurationPhrase(3600));
            Assert.AreEqual("1 hour", PvpArenaText.LockoutDurationPhrase(3600), "control: the deserter sibling does read in hours");
        }

        [TestMethod]
        public void RefusalText_DeserterLockout_IsTheExactLine()
        {
            var r = new PvpJoinResult(PvpJoinRefusal.DeserterLockout, "bg", LockoutSecondsRemaining: 3500);

            Assert.AreEqual("[Arena] You left a recent match early. You can queue for battlegrounds or Tugak Brawl again in 59 minutes.", PvpMatchCoordinator.RefusalText(r));
        }

        // ================= record dispatcher =================

        /// <summary>
        /// A scripted live server: the player's queue and the world-thread fallbacks are held until the test runs them. WriteOffline can be
        /// told to fail PART WAY (it stores the strike count, then throws) for its first N calls.
        /// </summary>
        private sealed class FakeTargets : IPvpDeserterWriteTargets
        {
            public bool Online;
            public bool QueueAccepts = true;
            public bool OnlineWriteSucceeds = true;
            public int OfflineFailuresLeft;

            public readonly List<Action> Queued = new();
            public readonly List<Action> Scheduled = new();
            public readonly List<string> Writes = new();

            /// <summary>The stored character's three values.</summary>
            public int Strikes;
            public long LastStrikeAt;
            public long LockoutUntil;

            public bool IsOnline(uint id) => Online;

            public bool LoggingOut;

            public bool IsLoggingOut(uint id) => Online && LoggingOut;

            public bool TryEnqueueOnPlayer(uint id, Action action)
            {
                if (!Online || LoggingOut || !QueueAccepts)
                    return false;

                Queued.Add(action);
                return true;
            }

            public bool WriteOnline(uint id, PvpDeserterRecord record)
            {
                Writes.Add("online");

                if (!OnlineWriteSucceeds)
                    return false;

                Store(record);
                return true;
            }

            public bool WriteOffline(uint id, PvpDeserterRecord record)
            {
                Writes.Add("offline");

                if (OfflineFailuresLeft > 0)
                {
                    OfflineFailuresLeft--;
                    Strikes = record.Strikes; // part way: the first property lands, then the save blows up
                    throw new InvalidOperationException("scripted partial write");
                }

                Store(record);
                return true;
            }

            public void ScheduleOnWorld(double seconds, Action action) => Scheduled.Add(action);

            private void Store(PvpDeserterRecord r)
            {
                Strikes = r.Strikes;
                LastStrikeAt = r.LastStrikeAt;
                LockoutUntil = r.LockoutUntil;
            }

            public PvpDeserterRecord Stored => new PvpDeserterRecord(Strikes, LastStrikeAt, LockoutUntil);

            public void RunScheduled()
            {
                var batch = Scheduled.ToList();
                Scheduled.Clear();

                foreach (var a in batch)
                    a();
            }
        }

        private static readonly PvpDeserterRecord Record = new PvpDeserterRecord(2, Now, Now + 3600);

        /// <summary>
        /// Offline, and the stored-character write fails PART WAY (the strike count lands, the times do not, then it throws): the record is
        /// re-pended, and the retry fallback rewrites all three absolute values. Nothing is left pending.
        /// </summary>
        [TestMethod]
        public void Offline_WriteFailsPartWay_ThenTheRetryStoresTheWholeRecord()
        {
            var t = new FakeTargets { Online = false, OfflineFailuresLeft = 1 };
            var d = new PvpDeserterRecordDispatcher(t);

            d.Persist(A, Record);

            Assert.AreEqual(new PvpDeserterRecord(2, 0, 0), t.Stored, "fixture: the first write landed only part way");
            Assert.AreEqual(1, d.PendingCount, "re-pended for a retry");
            Assert.AreEqual(2, d.AttemptOf(A));
            Assert.AreEqual(1, t.Scheduled.Count, "a fallback is armed");

            t.RunScheduled();

            Assert.AreEqual(Record, t.Stored, "the retry rewrote every value");
            Assert.AreEqual(0, d.PendingCount);
            CollectionAssert.AreEqual(new[] { "offline", "offline" }, t.Writes);
        }

        /// <summary>Online, but the player logged out under the queued write: the fallback stores it on the offline character.</summary>
        [TestMethod]
        public void Online_PlayerGoneWhenTheQueueRuns_FallsBackToTheStoredCharacter()
        {
            var t = new FakeTargets { Online = true, OnlineWriteSucceeds = false };
            var d = new PvpDeserterRecordDispatcher(t);

            d.Persist(A, Record);
            Assert.AreEqual(1, t.Queued.Count);

            t.Queued[0]();
            Assert.AreEqual(1, d.PendingCount, "the failed online write is re-pended");

            // The first fallback belongs to the claimed generation: it finds nothing. The retry's own fallback settles it.
            t.QueueAccepts = false;
            t.RunScheduled();

            Assert.AreEqual(Record, t.Stored);
            Assert.AreEqual(0, d.PendingCount);
            CollectionAssert.AreEqual(new[] { "online", "offline" }, t.Writes);
        }

        /// <summary>Online and healthy: the queue writes it once, and the armed fallback then finds nothing to do.</summary>
        [TestMethod]
        public void Online_TheQueueWritesOnce_TheFallbackIsANoOp()
        {
            var t = new FakeTargets { Online = true };
            var d = new PvpDeserterRecordDispatcher(t);

            d.Persist(A, Record);
            t.Queued.Single()();
            t.RunScheduled();

            Assert.AreEqual(Record, t.Stored);
            CollectionAssert.AreEqual(new[] { "online" }, t.Writes);
        }

        /// <summary>A newer record for the same character replaces the pending one: the older queued write and fallback do nothing.</summary>
        [TestMethod]
        public void ANewerRecord_SupersedesThePendingOne()
        {
            var t = new FakeTargets { Online = true };
            var d = new PvpDeserterRecordDispatcher(t);
            var newer = new PvpDeserterRecord(3, Now + 10, Now + 10 + 86400);

            d.Persist(A, Record);
            d.Persist(A, newer);

            foreach (var q in t.Queued.ToList())
                q();
            t.RunScheduled();

            Assert.AreEqual(newer, t.Stored);
            CollectionAssert.AreEqual(new[] { "online" }, t.Writes, "written once, and only the newer one");
        }

        /// <summary>A write that never lands is given up after MaxAttempts, with nothing left pending.</summary>
        [TestMethod]
        public void NeverLands_GivesUpAfterMaxAttempts()
        {
            var t = new FakeTargets { Online = false, OfflineFailuresLeft = int.MaxValue };
            var d = new PvpDeserterRecordDispatcher(t);

            d.Persist(A, Record);

            for (var i = 0; i < PvpDeserterRecordDispatcher.MaxAttempts + 5 && t.Scheduled.Count > 0; i++)
                t.RunScheduled();

            Assert.AreEqual(PvpDeserterRecordDispatcher.MaxAttempts, t.Writes.Count);
            Assert.AreEqual(0, d.PendingCount);
        }

        /// <summary>A clear for an OFFLINE character: the zeroed record replaces the stored strike values on the stored character.</summary>
        [TestMethod]
        public void Offline_Clear_ZeroesTheStoredRecord()
        {
            var t = new FakeTargets { Online = false, Strikes = 3, LastStrikeAt = Now - 60, LockoutUntil = Now + 86400 };
            var d = new PvpDeserterRecordDispatcher(t);
            var cleared = new PvpDeserterRecord(0, Now, 0);

            d.Persist(A, cleared);

            Assert.AreEqual(cleared, t.Stored);
            CollectionAssert.AreEqual(new[] { "offline" }, t.Writes);
            Assert.AreEqual(0, d.PendingCount);
        }

        /// <summary>Logging out counts as offline: the stored character is written at once, nothing is queued on the dying player queue.</summary>
        [TestMethod]
        public void LoggingOut_WritesOfflineFirst()
        {
            var t = new FakeTargets { Online = true, LoggingOut = true };
            var d = new PvpDeserterRecordDispatcher(t);

            d.Persist(A, Record);

            Assert.AreEqual(Record, t.Stored);
            CollectionAssert.AreEqual(new[] { "offline" }, t.Writes);
            Assert.AreEqual(0, t.Queued.Count);
            Assert.AreEqual(0, t.Scheduled.Count, "no 30 s fallback needed");
        }

        /// <summary>Logging out with no offline record yet: the immediate write fails and the 30 s fallback stores it.</summary>
        [TestMethod]
        public void LoggingOut_NoOfflineRecordYet_FallsBackToTheTimer()
        {
            var t = new FakeTargets { Online = true, LoggingOut = true, OfflineFailuresLeft = 1 };
            var d = new PvpDeserterRecordDispatcher(t);

            d.Persist(A, Record);
            Assert.AreEqual(1, t.Scheduled.Count, "fixture: the immediate write failed, a fallback is armed");

            t.RunScheduled();

            Assert.AreEqual(Record, t.Stored);
            CollectionAssert.AreEqual(new[] { "offline", "offline" }, t.Writes);
        }

        // ================= /arenaadmin deserter (pure parts) =================

        private static (string Name, bool Clear) Parse(string[] args, params string[] names) =>
            ACE.Server.Command.Handlers.PvpArenaAdminCommands.ParseDeserterArgs(args, n => names.Contains(n, StringComparer.OrdinalIgnoreCase));

        /// <summary>The whole text is looked up first: with both "Crystal" and "Crystal Clear" on the shard, "Crystal Clear" is that character.</summary>
        [TestMethod]
        public void ParseDeserterArgs_FullNameWins_WhenItResolves()
        {
            Assert.AreEqual(("Crystal Clear", false), Parse(new[] { "Crystal", "Clear" }, "Crystal", "Crystal Clear"));
            Assert.AreEqual(("Crystal Clear", true), Parse(new[] { "Crystal", "Clear", "clear" }, "Crystal", "Crystal Clear"), "clearing Crystal Clear takes a second clear");
        }

        /// <summary>Only "Crystal" exists: the trailing word is the clear request.</summary>
        [TestMethod]
        public void ParseDeserterArgs_TrailingClear_WhenTheFullNameDoesNotResolve()
        {
            Assert.AreEqual(("Crystal", true), Parse(new[] { "Crystal", "Clear" }, "Crystal"));
            Assert.AreEqual(("Sir Bob", false), Parse(new[] { "Sir", "Bob" }, "Sir Bob"));
            Assert.AreEqual(("clear", false), Parse(new[] { "clear" }, "Crystal"), "a lone word is the name");
            Assert.AreEqual(((string)null, false), Parse(new string[0], "Crystal"));
        }

        [DataTestMethod]
        [DataRow(0L, "none")]
        [DataRow(-5L, "none")]
        [DataRow(1_800_000_000L, "2027-01-15 08:00:00 UTC")]
        [DataRow(long.MaxValue, "out of range (9223372036854775807)")]
        [DataRow(253402300800L, "out of range (253402300800)")]
        public void FormatUnixUtc_NeverThrows(long value, string expected)
        {
            Assert.AreEqual(expected, ACE.Server.Command.Handlers.PvpArenaAdminCommands.FormatUnixUtc(value));
        }

        // ================= dial bounds =================

        [TestMethod]
        public void Clamp_BoundsTheDeserterDials()
        {
            var c = PvpConductTunables.Clamp(Dials with { DeserterLockout1Minutes = long.MaxValue, DeserterLockout2Minutes = -3, DeserterLockout3Minutes = 600000, DeserterDecayDays = long.MaxValue });

            Assert.AreEqual(525600, c.DeserterLockout1Minutes);
            Assert.AreEqual(0, c.DeserterLockout2Minutes);
            Assert.AreEqual(525600, c.DeserterLockout3Minutes);
            Assert.AreEqual(36500, c.DeserterDecayDays);
        }

        /// <summary>Unclamped dials reaching the ladder directly are bounded at use too: no overflow, a year at most.</summary>
        [TestMethod]
        public void Ladder_BoundsUnclampedDialsAtUse()
        {
            var raw = Dials with { DeserterLockout1Minutes = long.MaxValue, DeserterDecayDays = long.MaxValue };

            Assert.AreEqual(525600, PvpDeserterLadder.LockoutMinutes(1, raw));
            Assert.AreEqual(2, PvpDeserterLadder.Decayed(2, Now - 1000 * Day, Now, long.MaxValue), "a 100-year period has not passed: nothing decays, and nothing overflows");
            Assert.AreEqual(new PvpDeserterRecord(1, Now, Now + 525600L * 60), PvpDeserterLadder.Next(0, 0, Now, raw));
        }

        // ================= server- and staff-caused logouts =================

        /// <summary>THE TERMINATION TABLE, pinned value by value; a new SessionTerminationReason fails here until it is classified.</summary>
        [TestMethod]
        public void IsServerCausedTermination_IsExhaustive()
        {
            var serverCaused = new[]
            {
                ACE.Server.Network.Enum.SessionTerminationReason.AccountBooted,
                ACE.Server.Network.Enum.SessionTerminationReason.ForcedLogOffRequested,
                ACE.Server.Network.Enum.SessionTerminationReason.AutoForcedLogOff,
                ACE.Server.Network.Enum.SessionTerminationReason.WorldClosed,
                ACE.Server.Network.Enum.SessionTerminationReason.ServerShuttingDown,
                ACE.Server.Network.Enum.SessionTerminationReason.CharacterSaveFailed,
                ACE.Server.Network.Enum.SessionTerminationReason.BiotaSaveFailed,
                ACE.Server.Network.Enum.SessionTerminationReason.AccountBanned,
            };

            foreach (var r in Enum.GetValues(typeof(ACE.Server.Network.Enum.SessionTerminationReason)).Cast<ACE.Server.Network.Enum.SessionTerminationReason>())
                Assert.AreEqual(serverCaused.Contains(r), PvpDeserterRules.IsServerCausedTermination(r), r.ToString());

            Assert.IsFalse(PvpDeserterRules.IsServerCausedTermination(ACE.Server.Network.Enum.SessionTerminationReason.AccountLoggedIn), "a second login on the account is the player's own act");
            Assert.IsFalse(PvpDeserterRules.IsServerCausedTermination(null), "no termination pending: an ordinary logout");
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PvpDeserterRules.IsServerCausedTermination((ACE.Server.Network.Enum.SessionTerminationReason)999));
        }

        [TestMethod]
        public void LogoutForfeit_CarriesServerCaused()
        {
            Assert.IsFalse(PvpMatchManager.LogoutForfeit(A, Guid.Empty, DateTime.UtcNow).ServerCaused);
            Assert.IsTrue(PvpMatchManager.LogoutForfeit(A, Guid.Empty, DateTime.UtcNow, serverCaused: true).ServerCaused);
        }

        [TestMethod]
        public void FormatDeserterStatus_IsTheExactLine()
        {
            var locked = new PvpDeserterStatus(new PvpDeserterRecord(2, 1_800_000_000, 1_800_003_600), 2, 3000);
            var none = new PvpDeserterStatus(PvpDeserterRecord.None, 0, 0);

            Assert.AreEqual(
                "[ArenaAdmin] Alpha deserter: strikes 2 (now 2 after decay), last strike 2027-01-15 08:00:00 UTC, locked until 2027-01-15 09:00:00 UTC, remaining 50 minutes.",
                ACE.Server.Command.Handlers.PvpArenaAdminCommands.FormatDeserterStatus("Alpha", locked));
            Assert.AreEqual(
                "[ArenaAdmin] Alpha deserter: strikes 0 (now 0 after decay), last strike none, locked until none, remaining none.",
                ACE.Server.Command.Handlers.PvpArenaAdminCommands.FormatDeserterStatus("Alpha", none));
        }

        private const uint A = 0x50000091;
    }
}
