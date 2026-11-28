using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The "locked into peace mode" mitigation and its diagnostics (CombatStanceSync, CombatModeRequestLog,
    /// StanceDesyncWatchdog). Each decision test pairs the outcome that must stay as before (monsters, NonCombat,
    /// real switches) with the outcome the change introduced, so it fails against the old silent short-circuit or a
    /// per-packet logger.
    /// </summary>
    [TestClass]
    public class CombatStanceSyncTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        // ---- M1: the SetCombatMode short-circuit ----

        [TestMethod]
        public void ShortCircuit_PlayerAlreadyInRequestedStance_ResendsToSelf()
        {
            // Joe isuzu, 2026-09-30: bow equipped, server already BowCombat, client asks for Missile again
            Assert.AreEqual(CombatStanceSync.StanceRequestAction.ResendToSelf,
                CombatStanceSync.ClassifyStanceRequest(true, CombatMode.Missile, MotionStance.BowCombat, MotionStance.BowCombat));
        }

        [TestMethod]
        public void ShortCircuit_MonsterAlreadyInRequestedStance_StaysSilent()
        {
            Assert.AreEqual(CombatStanceSync.StanceRequestAction.Silent,
                CombatStanceSync.ClassifyStanceRequest(false, CombatMode.Missile, MotionStance.BowCombat, MotionStance.BowCombat));
        }

        [TestMethod]
        public void ShortCircuit_NonCombatRequest_AlwaysSwitches_ForPlayerAndMonster()
        {
            Assert.AreEqual(CombatStanceSync.StanceRequestAction.Switch,
                CombatStanceSync.ClassifyStanceRequest(true, CombatMode.NonCombat, MotionStance.NonCombat, MotionStance.NonCombat));
            Assert.AreEqual(CombatStanceSync.StanceRequestAction.Switch,
                CombatStanceSync.ClassifyStanceRequest(false, CombatMode.NonCombat, MotionStance.NonCombat, MotionStance.NonCombat));
        }

        [TestMethod]
        public void ShortCircuit_DifferentStance_Switches_ForPlayerAndMonster()
        {
            Assert.AreEqual(CombatStanceSync.StanceRequestAction.Switch,
                CombatStanceSync.ClassifyStanceRequest(true, CombatMode.Missile, MotionStance.NonCombat, MotionStance.BowCombat));
            Assert.AreEqual(CombatStanceSync.StanceRequestAction.Switch,
                CombatStanceSync.ClassifyStanceRequest(false, CombatMode.Melee, MotionStance.NonCombat, MotionStance.SwordCombat));
        }

        [TestMethod]
        public void SetCombatMode_RoutesItsShortCircuitThroughTheHelper_AndResendsToSelfOnly()
        {
            // binds the pure decision above to the call site it is meant to govern
            var source = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Creature_Combat.cs")));

            var setCombatMode = ExtractMember(source, "public float SetCombatMode(CombatMode combatMode, out float queueTime");
            StringAssert.Contains(setCombatMode, "CombatStanceSync.ClassifyStanceRequest(player != null, combatMode, CurrentMotionState.Stance, combatStance)");
            StringAssert.Contains(setCombatMode, "ResendStanceToSelf(player, combatMode, combatStance, animOnly)");
            Assert.IsFalse(Regex.IsMatch(setCombatMode, @"if \(combatMode != CombatMode\.NonCombat && CurrentMotionState\.Stance == combatStance\)"),
                "the old silent short-circuit is back in SetCombatMode");

            var resend = ExtractMember(source, "protected void ResendStanceToSelf(");
            StringAssert.Contains(resend, "player.Session.Network.EnqueueSend(");
            StringAssert.Contains(resend, "new GameMessageUpdateMotion(this, new Motion(combatStance))");
            Assert.IsFalse(resend.Contains("EnqueueBroadcast"), "the re-send must go to the player's own client only");
        }

        // ---- client stance read from MoveToState ----

        [TestMethod]
        public void ClientReportedStance_FlagAbsent_IsNonCombat_FlagPresent_IsTheValue()
        {
            Assert.AreEqual(MotionStance.NonCombat, CombatStanceSync.ClientReportedStance(false, MotionStance.BowCombat));
            Assert.AreEqual(MotionStance.BowCombat, CombatStanceSync.ClientReportedStance(true, MotionStance.BowCombat));
            Assert.AreEqual(MotionStance.NonCombat, CombatStanceSync.ClientReportedStance(true, 0));
        }

        // ---- [COMBATMODE] repeat detector and rate cap ----

        [TestMethod]
        public void RequestLog_SameModeWithinWindow_IsRepeat_OtherwiseNot()
        {
            var log = new CombatModeRequestLog();

            Assert.IsFalse(log.RecordRequest(T0, CombatMode.Missile));
            Assert.IsTrue(log.RecordRequest(T0.AddSeconds(3), CombatMode.Missile));
            Assert.IsFalse(log.RecordRequest(T0.AddSeconds(4), CombatMode.NonCombat));
            Assert.IsFalse(log.RecordRequest(T0.AddSeconds(30), CombatMode.NonCombat));
        }

        [TestMethod]
        public void RequestLog_CapsInfoLinesPerMinute_AndReportsTheDroppedCount()
        {
            var log = new CombatModeRequestLog();

            for (var i = 0; i < CombatModeRequestLog.MaxInfoLinesPerWindow; i++)
                Assert.IsTrue(log.TryTakeInfoLine(T0.AddSeconds(i), out _), $"line {i} should pass");

            Assert.IsFalse(log.TryTakeInfoLine(T0.AddSeconds(20), out _));
            Assert.IsFalse(log.TryTakeInfoLine(T0.AddSeconds(21), out _));

            Assert.IsTrue(log.TryTakeInfoLine(T0.AddSeconds(61), out var suppressed));
            Assert.AreEqual(2, suppressed);
        }

        // ---- [STANCE_DESYNC] watchdog ----

        [TestMethod]
        public void Watchdog_ReportsOnceAfterThreshold_NotPerPacket_ThenOnceOnRecovery()
        {
            var w = new StanceDesyncWatchdog();
            var starts = 0;

            // a mismatched packet every 500 ms for 20 s
            for (var ms = 0; ms <= 20000; ms += 500)
            {
                var t = w.Observe(T0.AddMilliseconds(ms), MotionStance.NonCombat, MotionStance.BowCombat, false, out _);
                if (t == StanceDesyncWatchdog.Transition.StreakStarted)
                {
                    starts++;
                    Assert.IsTrue(ms >= StanceDesyncWatchdog.Threshold.TotalMilliseconds, $"started early at {ms} ms");
                }
                else
                    Assert.AreEqual(StanceDesyncWatchdog.Transition.None, t);
            }

            Assert.AreEqual(1, starts);

            var recovered = w.Observe(T0.AddSeconds(25), MotionStance.BowCombat, MotionStance.BowCombat, false, out var duration);
            Assert.AreEqual(StanceDesyncWatchdog.Transition.Recovered, recovered);
            Assert.AreEqual(TimeSpan.FromSeconds(25), duration);

            // in sync afterwards: silent
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None,
                w.Observe(T0.AddSeconds(26), MotionStance.BowCombat, MotionStance.BowCombat, false, out _));
        }

        [TestMethod]
        public void Watchdog_ShortMismatch_UnderThreshold_NeverReports()
        {
            var w = new StanceDesyncWatchdog();

            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0, MotionStance.NonCombat, MotionStance.Magic, false, out _));
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(4), MotionStance.NonCombat, MotionStance.Magic, false, out _));
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(4.5), MotionStance.Magic, MotionStance.Magic, false, out _));

            // the clock restarted when it came back into sync
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(6), MotionStance.NonCombat, MotionStance.Magic, false, out _));
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(10), MotionStance.NonCombat, MotionStance.Magic, false, out _));
            Assert.AreEqual(StanceDesyncWatchdog.Transition.StreakStarted, w.Observe(T0.AddSeconds(11), MotionStance.NonCombat, MotionStance.Magic, false, out _));
        }

        [TestMethod]
        public void Watchdog_SuppressedObservations_NeverStartAStreak_AndRestartTheClock()
        {
            var w = new StanceDesyncWatchdog();

            for (var s = 0; s <= 20; s++)
                Assert.AreEqual(StanceDesyncWatchdog.Transition.None,
                    w.Observe(T0.AddSeconds(s), MotionStance.NonCombat, MotionStance.BowCombat, true, out _));

            // unsuppressed from 21 s: needs a full threshold of its own
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(21), MotionStance.NonCombat, MotionStance.BowCombat, false, out _));
            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(25), MotionStance.NonCombat, MotionStance.BowCombat, false, out _));
            Assert.AreEqual(StanceDesyncWatchdog.Transition.StreakStarted, w.Observe(T0.AddSeconds(26), MotionStance.NonCombat, MotionStance.BowCombat, false, out _));
        }

        [TestMethod]
        public void Watchdog_SuppressionDoesNotCloseAnOpenStreak()
        {
            var w = new StanceDesyncWatchdog();

            w.Observe(T0, MotionStance.NonCombat, MotionStance.BowCombat, false, out _);
            Assert.AreEqual(StanceDesyncWatchdog.Transition.StreakStarted, w.Observe(T0.AddSeconds(6), MotionStance.NonCombat, MotionStance.BowCombat, false, out _));

            Assert.AreEqual(StanceDesyncWatchdog.Transition.None, w.Observe(T0.AddSeconds(7), MotionStance.NonCombat, MotionStance.BowCombat, true, out _));
            Assert.IsTrue(w.InStreak);
        }

        [TestMethod]
        public void Watchdog_InvalidServerStance_IsNotADisagreement()
        {
            var w = new StanceDesyncWatchdog();

            for (var s = 0; s <= 20; s++)
                Assert.AreEqual(StanceDesyncWatchdog.Transition.None,
                    w.Observe(T0.AddSeconds(s), MotionStance.NonCombat, MotionStance.Invalid, false, out _));
        }

        // ---- R1: the request log is shared by the world thread and the landblock tick ----

        [TestMethod]
        public void RequestLog_TwoThreads_NeverExceedTheCapInAnyWindow()
        {
            // a deferred request runs from the player's action queue (landblock tick) while a new one arrives on the
            // world thread. Each trial is one fresh window that both threads hit at the same instant, racing through
            // the first-call rollover and the cap check together.
            const int trials = 20000;
            const int callsPerThread = 12;

            CombatModeRequestLog log = null;
            var totals = new int[trials];

            // phases alternate start/end of a trial; the post-phase action of every start phase installs a fresh log,
            // so both threads enter each trial against a brand-new window
            var phase = 0;
            var barrier = new System.Threading.Barrier(2, _ =>
            {
                if (phase % 2 == 0)
                    System.Threading.Volatile.Write(ref log, new CombatModeRequestLog());
                phase++;
            });

            void Run()
            {
                for (var trial = 0; trial < trials; trial++)
                {
                    barrier.SignalAndWait();

                    var current = System.Threading.Volatile.Read(ref log);

                    for (var i = 0; i < callsPerThread; i++)
                    {
                        if (current.TryTakeInfoLine(T0, out _))
                            System.Threading.Interlocked.Increment(ref totals[trial]);
                    }

                    barrier.SignalAndWait();
                }
            }


            var a = new System.Threading.Thread(Run);
            var b = new System.Threading.Thread(Run);
            a.Start(); b.Start();
            a.Join(); b.Join();
            barrier.Dispose();

            for (var trial = 0; trial < trials; trial++)
                Assert.IsTrue(totals[trial] <= CombatModeRequestLog.MaxInfoLinesPerWindow, $"trial {trial}: one window emitted {totals[trial]} lines");
        }
        // ---- R2: the start cap ----

        [TestMethod]
        public void Watchdog_FlappingDesync_ReportsAtMostOneStartPerMinute_AndCountsTheRest()
        {
            var w = new StanceDesyncWatchdog();
            var starts = 0;
            var recoveries = 0;
            var suppressedReported = 0;

            // 6 s mismatched, 1 s in sync, repeated for 10 minutes; a packet every 500 ms
            for (var ms = 0; ms < 10 * 60 * 1000; ms += 500)
            {
                var inSync = ms % 7000 >= 6000;
                var t = w.Observe(T0.AddMilliseconds(ms), inSync ? MotionStance.BowCombat : MotionStance.NonCombat, MotionStance.BowCombat, false, out _);

                if (t == StanceDesyncWatchdog.Transition.StreakStarted)
                {
                    starts++;
                    suppressedReported += w.SuppressedStartsBeforeLast;
                }
                else if (t == StanceDesyncWatchdog.Transition.Recovered)
                    recoveries++;
            }

            Assert.IsTrue(starts >= 1 && starts <= 10, $"reported {starts} starts");
            Assert.AreEqual(starts, recoveries, "only reported streaks report a recovery");
            Assert.IsTrue(suppressedReported > 0, "suppressed starts must be counted into a later reported line");
        }

        // ---- T1: wiring ----

        [TestMethod]
        public void Wiring_OnMoveToState_ObservesBeforeTheFastTickReturn()
        {
            var onMoveToState = ExtractMember(PlayerTickSource(), "public void OnMoveToState(MoveToState moveToState)");

            var observe = onMoveToState.IndexOf("ObserveStanceDesync(moveToState);", StringComparison.Ordinal);
            var fastTick = onMoveToState.IndexOf("if (!FastTick)", StringComparison.Ordinal);

            Assert.IsTrue(observe >= 0, "OnMoveToState no longer feeds the watchdog");
            Assert.IsTrue(fastTick >= 0, "FastTick early return not found");
            Assert.IsTrue(observe < fastTick, "the watchdog must run before the FastTick early return");
        }

        [TestMethod]
        public void Wiring_ObserveStanceDesync_SuppressesDuringTeleportAndAnimation()
        {
            var observe = ExtractMember(PlayerTickSource(), "private void ObserveStanceDesync(MoveToState moveToState)");

            StringAssert.Contains(observe, "stanceDesyncWatchdog.Observe(now, clientStance, serverStance, Teleporting || now < NextUseTime, out var duration)");
        }

        [TestMethod]
        public void Wiring_ResendStanceToSelf_SyncsCombatModeOnlyWhenNotAnimOnly()
        {
            var source = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Creature_Combat.cs")));
            var resend = ExtractMember(source, "protected void ResendStanceToSelf(");

            Assert.IsTrue(Regex.IsMatch(resend, @"if \(!animOnly\)\s*CombatMode = combatMode;"), "CombatMode must only be synced when !animOnly");
            Assert.AreEqual(1, Regex.Matches(resend, @"CombatMode = combatMode;").Count, "no unguarded CombatMode assignment");
        }

        [TestMethod]
        public void Wiring_Heartbeat_WarnsTeleportStuckBeforeForcedLogoff()
        {
            var heartbeat = ExtractMember(PlayerTickSource(), "public override void Heartbeat(double currentUnixTime)");

            var warn = heartbeat.IndexOf("[TELEPORT_STUCK]", StringComparison.Ordinal);
            var logoff = heartbeat.IndexOf("Session.LogOffPlayer(true)", StringComparison.Ordinal);

            Assert.IsTrue(warn >= 0, "[TELEPORT_STUCK] warning missing from Heartbeat");
            Assert.IsTrue(logoff >= 0, "forced logoff not found in Heartbeat");
            Assert.IsTrue(warn < logoff, "[TELEPORT_STUCK] must be logged before the forced logoff");
        }

        // ---- portal arrival resync ----

        [TestMethod]
        public void Wiring_OnTeleportComplete_ResyncsStanceAfterMaterializeNotInPinkBubbleRetry()
        {
            var onComplete = ExtractMember(PlayerLocationSource(), "public void OnTeleportComplete()");

            var retry = onComplete.IndexOf("actionChain.AddAction(this, OnTeleportComplete);", StringComparison.Ordinal);
            var teleportingFalse = onComplete.IndexOf("Teleporting = false;", StringComparison.Ordinal);
            var resync = onComplete.IndexOf("ScheduleArrivalStanceResync();", StringComparison.Ordinal);
            var delayed = onComplete.IndexOf("ScheduleDelayedStanceResync();", StringComparison.Ordinal);

            Assert.IsTrue(retry >= 0, "pink-bubble retry not found");
            Assert.IsTrue(teleportingFalse >= 0, "Teleporting = false not found");
            Assert.IsTrue(resync >= 0, "deferred arrival stance resync missing from OnTeleportComplete");
            Assert.IsTrue(delayed >= 0, "delayed stance resync missing from OnTeleportComplete");
            Assert.AreEqual(0, Regex.Matches(onComplete, @"ResyncStanceToSelf\(").Count, "no immediate resync: it would overtake an autorun MoveToState");
            Assert.AreEqual(1, Regex.Matches(onComplete, @"ScheduleArrivalStanceResync\(\);").Count, "exactly one deferred arrival resync");
            Assert.IsTrue(resync > teleportingFalse, "the resync must come AFTER Teleporting = false");
            Assert.IsTrue(delayed > teleportingFalse, "the delayed resync must be scheduled AFTER Teleporting = false");
            Assert.IsTrue(resync > retry, "the resync must not sit in (or before) the pink-bubble retry branch");

            // the retry branch ends at its own return; nothing resync-related may appear before it
            var retryReturn = onComplete.IndexOf("return;", retry, StringComparison.Ordinal);
            Assert.IsTrue(retryReturn >= 0 && resync > retryReturn, "the resync must come after the retry branch's return");
        }

        private const double Still = 0.1, GiveUp = 60.0;

        [TestMethod]
        public void ArrivalResync_Moving_IsInWindowMovementOrServerMoveTo()
        {
            Assert.IsTrue(CombatStanceSync.IsMovingForArrivalResync(true, true, false), "autorun reported this window");
            Assert.IsTrue(CombatStanceSync.IsMovingForArrivalResync(false, false, true), "server MoveTo");

            Assert.IsFalse(CombatStanceSync.IsMovingForArrivalResync(true, false, false), "a stop report this window is idle");
            Assert.IsFalse(CombatStanceSync.IsMovingForArrivalResync(false, false, false), "no report");
        }

        [TestMethod]
        public void ArrivalResync_PreWindowMovementReport_IsIdleAtTheDeferredSend()
        {
            const double windowStart = 1000.0;

            // the run into the portal: a movement report stamped before the teleport started
            var reported = CombatStanceSync.ClientReportedThisWindow(windowStart - 0.5, windowStart);
            Assert.IsFalse(reported);
            Assert.IsFalse(CombatStanceSync.IsMovingForArrivalResync(reported, true, false), "a stale movement report must not hold the 0.5 s send back");

            // the same report made after the teleport began does hold it back
            reported = CombatStanceSync.ClientReportedThisWindow(windowStart + 0.2, windowStart);
            Assert.IsTrue(CombatStanceSync.IsMovingForArrivalResync(reported, true, false));

            Assert.IsTrue(CombatStanceSync.ClientReportedThisWindow(windowStart, windowStart), "same instant counts");
            Assert.IsFalse(CombatStanceSync.ClientReportedThisWindow(0, windowStart), "0 = no MoveToState handled yet");
            Assert.IsFalse(CombatStanceSync.ClientReportedThisWindow(0, 0), "0 stays unset even against a zero window");
            Assert.IsFalse(CombatStanceSync.ClientReportedThisWindow(windowStart + 0.5, null), "no window");
        }

        private const int StillTicks = 2;

        /// <summary>Drives the retry the way Player.ScheduleArrivalStanceResyncRetry does; returns the tick that sent, or null.</summary>
        private static int? RunRetry(bool moving, double?[] movedPerTick, bool acting = false)
        {
            var still = 0;

            for (var tick = 0; tick < movedPerTick.Length; tick++)
            {
                still = CombatStanceSync.NextStillTicks(still, movedPerTick[tick], Still);
                var step = CombatStanceSync.DecideArrivalResyncRetry(true, false, moving, acting, still, StillTicks, 1.5 + tick, GiveUp);

                if (step == CombatStanceSync.ArrivalResyncStep.Send)
                    return tick;

                if (step == CombatStanceSync.ArrivalResyncStep.Stop)
                    return null;
            }

            return null;
        }

        [TestMethod]
        public void ArrivalResync_Retry_ContinuouslyMovingPlayer_NeverGetsASend()
        {
            // autorun for well past the give-up time: about 5 m covered per 1 s tick
            var running = new double?[70];
            for (var i = 0; i < running.Length; i++)
                running[i] = 5.0;
            Assert.IsNull(RunRetry(true, running), "a running player must never be interrupted");

            // a running player whose Location reads unchanged on ONE tick (no position report landed) is still running
            Assert.IsNull(RunRetry(true, new double?[] { 5.0, 0.0, 5.0, 0.0, 5.0, 0.0, 5.0 }), "one identical-position tick must not send");

            // slow but real movement just over the threshold, and unknown positions, are never still
            Assert.IsNull(RunRetry(true, new double?[] { Still, Still, Still, Still }));
            Assert.IsNull(RunRetry(true, new double?[] { null, null, null, null }));

            // past the give-up time it stops without a send
            Assert.AreEqual(CombatStanceSync.ArrivalResyncStep.Stop, CombatStanceSync.DecideArrivalResyncRetry(true, false, true, false, 0, StillTicks, GiveUp, GiveUp));
        }

        [TestMethod]
        public void ArrivalResync_Retry_StuckFlagButStationary_SendsAtTheSecondConsecutiveStillTick()
        {
            // moving flag stuck true throughout; runs for 3 ticks, then stands still
            Assert.AreEqual(4, RunRetry(true, new double?[] { 5.0, 5.0, 5.0, 0.02, 0.0, 0.0 }), "two consecutive still ticks send");

            // the NextStillTicks counter resets on any real movement
            Assert.AreEqual(0, CombatStanceSync.NextStillTicks(1, 5.0, Still));
            Assert.AreEqual(2, CombatStanceSync.NextStillTicks(1, 0.0, Still));
            Assert.AreEqual(0, CombatStanceSync.NextStillTicks(1, null, Still));

            // not moving at all sends at once; mid-action waits; stop conditions win over everything
            Assert.AreEqual(0, RunRetry(false, new double?[] { 5.0 }));
            Assert.IsNull(RunRetry(false, new double?[] { 0.0, 0.0, 0.0 }, acting: true), "casting/attacking waits");
            Assert.AreEqual(CombatStanceSync.ArrivalResyncStep.Stop, CombatStanceSync.DecideArrivalResyncRetry(false, false, false, false, 5, StillTicks, 2, GiveUp), "window gone");
            Assert.AreEqual(CombatStanceSync.ArrivalResyncStep.Stop, CombatStanceSync.DecideArrivalResyncRetry(true, true, false, false, 5, StillTicks, 2, GiveUp), "already resynced");
        }

        [TestMethod]
        public void ArrivalResync_StopReport_TriggersTheFastPathOnce()
        {
            // pending after the deferred skip; reports: still running, running, stop, stop, start again, stop
            var reportsHaveMovement = new[] { true, true, false, false, true, false };
            var pending = true;
            var sends = 0;
            var sentOnReport = -1;

            for (var i = 0; i < reportsHaveMovement.Length; i++)
            {
                if (CombatStanceSync.ShouldSendPendingResyncOnReport(pending, reportsHaveMovement[i], false, false))
                {
                    sends++;
                    sentOnReport = i;
                    pending = false; // the send claims (clears) the pending state
                }
            }

            Assert.AreEqual(1, sends, "exactly one fast-path send per window");
            Assert.AreEqual(2, sentOnReport, "sent on the first stop report");
            Assert.IsFalse(CombatStanceSync.ShouldSendPendingResyncOnReport(true, false, true, false), "a server MoveTo holds it");
            Assert.IsFalse(CombatStanceSync.ShouldSendPendingResyncOnReport(true, false, false, true), "mid-action holds it; the retry covers it");
        }

        [TestMethod]
        public void Wiring_ArrivalResync_DeferredGuardRetryAndFastPath()
        {
            var source = PlayerLocationSource();

            StringAssert.Contains(source, "private const double ArrivalStanceResyncDelaySeconds = 0.5;");
            StringAssert.Contains(source, "private const double ArrivalStanceResyncRetrySeconds = 1.0;");
            StringAssert.Contains(source, "private const double ArrivalStanceResyncGiveUpSeconds = 60.0;");
            StringAssert.Contains(source, "private const double ArrivalStanceResyncStillMeters = 0.1;");

            var moving = ExtractMember(source, "private bool IsMovingForArrivalStanceResync(double? stamp)");
            StringAssert.Contains(moving, "CombatStanceSync.ClientReportedThisWindow(LastMoveToStateTime, stamp)");
            StringAssert.Contains(moving, "CombatStanceSync.IsMovingForArrivalResync(");

            var deferred = ExtractMember(source, "private void ScheduleArrivalStanceResync()");
            StringAssert.Contains(deferred, "chain.AddDelaySeconds(ArrivalStanceResyncDelaySeconds);");
            var action = deferred.IndexOf("chain.AddAction(this, () =>", StringComparison.Ordinal);
            var gate = Regex.Match(deferred, @"if \(!IsMovingForArrivalStanceResync\(stamp\)\)\s*\{\s*ResyncStanceToSelf\(""teleport-complete""\);\s*return;\s*\}");
            Assert.IsTrue(action >= 0 && gate.Success && gate.Index > action, "the deferred send must be gated at fire time");
            var tail = deferred.Substring(gate.Index + gate.Length);
            StringAssert.Contains(tail, "Interlocked.Exchange(ref pendingArrivalStanceResyncStamp, stamp ?? 0);");
            StringAssert.Contains(tail, "ScheduleArrivalStanceResyncRetry(stamp, arrivedAt, Location == null ? null : new Position(Location), 0);");
            StringAssert.Contains(source, "private const int ArrivalStanceResyncStillTicksRequired = 2;");
            StringAssert.Contains(source, "private double pendingArrivalStanceResyncStamp;");

            var retryBody = ExtractMember(source, "private void ScheduleArrivalStanceResyncRetry(double? stamp, double arrivedAt, Position previous, int stillTicks)");
            StringAssert.Contains(retryBody, "chain.AddDelaySeconds(ArrivalStanceResyncRetrySeconds);");
            StringAssert.Contains(retryBody, "current.DistanceTo(previous)");
            StringAssert.Contains(retryBody, "IsMovingForArrivalStanceResync(stamp)");
            StringAssert.Contains(retryBody, "ArrivalStanceResyncGiveUpSeconds);");
            StringAssert.Contains(retryBody, "CombatStanceSync.NextStillTicks(stillTicks, moved, ArrivalStanceResyncStillMeters)");
            StringAssert.Contains(retryBody, "ArrivalStanceResyncStillTicksRequired,");
            Assert.IsTrue(Regex.IsMatch(retryBody, @"case CombatStanceSync\.ArrivalResyncStep\.Send:\s*if \(TryClaimPendingArrivalStanceResync\(stamp\)\)\s*ResyncStanceToSelf\("), "Send must claim, then resync");
            Assert.IsTrue(Regex.IsMatch(retryBody, @"case CombatStanceSync\.ArrivalResyncStep\.Wait:\s*ScheduleArrivalStanceResyncRetry\(stamp, arrivedAt, current, consecutiveStill\);"), "Wait must re-arm with this tick's position and still count");

            var claim = ExtractMember(source, "private bool TryClaimPendingArrivalStanceResync(double? stamp)");
            StringAssert.Contains(claim, "Interlocked.CompareExchange(ref pendingArrivalStanceResyncStamp, 0, stamp.Value) == stamp.Value");

            var resync = ExtractMember(source, "public void ResyncStanceToSelf(string reason)");
            StringAssert.Contains(resync, "lastStanceResyncTime = Time.GetUnixTime();");
            StringAssert.Contains(resync, "Interlocked.Exchange(ref pendingArrivalStanceResyncStamp, 0);");
            StringAssert.Contains(retryBody, "lastStanceResyncTime >= arrivedAt");

            var fast = ExtractMember(source, "public void CheckPendingArrivalStanceResync(ACE.Server.Network.Structure.MoveToState moveToState)");
            StringAssert.Contains(fast, "CombatStanceSync.ShouldSendPendingResyncOnReport(");
            StringAssert.Contains(fast, "MoveToParams != null || IsPlayerMovingTo, IsActingForStanceResync()))");
            Assert.IsTrue(Regex.IsMatch(fast, @"if \(TryClaimPendingArrivalStanceResync\(stamp\)\)\s*ResyncStanceToSelf\(""teleport-complete-stopped""\);"), "the fast path must claim before sending");
            StringAssert.Contains(fast, "ResyncStanceToSelf(\"teleport-complete-stopped\");");

            var handler = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "Network", "GameAction", "Actions", "GameActionMoveToState.cs")));
            Assert.IsTrue(Regex.IsMatch(handler, @"session\.Player\.LastMoveToState = moveToState;\s*session\.Player\.LastMoveToStateTime = Time\.GetUnixTime\(\);"),
                "GameActionMoveToState must stamp LastMoveToStateTime together with LastMoveToState");
            var stamped = handler.IndexOf("session.Player.LastMoveToStateTime = Time.GetUnixTime();", StringComparison.Ordinal);
            var fastCall = handler.IndexOf("session.Player.CheckPendingArrivalStanceResync(moveToState);", StringComparison.Ordinal);
            Assert.IsTrue(fastCall > stamped, "the fast path runs on every report, after LastMoveToState is stored");
        }

        [TestMethod]
        public void Wiring_DelayedStanceResync_SkipsWhenTeleportingNoNetworkOrLoggingOut()
        {
            var delayed = ExtractMember(PlayerLocationSource(), "private void ScheduleDelayedStanceResync()");

            var send = delayed.IndexOf("ResyncStanceToSelf(", StringComparison.Ordinal);
            Assert.IsTrue(send >= 0, "the delayed resync does not send");

            // every guard must actually return, and must sit before the send
            var guards = new[]
            {
                "Teleporting || Session?.Network == null || IsLoggingOut",
                "IsDead || IsInDeathProcess",
                "DateTime.UtcNow < NextUseTime || MagicState.IsCasting || Attacking",
                "MoveToParams != null || IsPlayerMovingTo || LastMoveToState?.RawMotionState?.HasMovement() == true",
            };

            foreach (var guard in guards)
            {
                var m = Regex.Match(delayed, @"if \(" + Regex.Escape(guard) + @"\)\s*return;");
                Assert.IsTrue(m.Success, $"the delayed resync must skip (and return) when: {guard}");
                Assert.IsTrue(m.Index < send, $"the guard '{guard}' must run before the send");
            }

            // the chain waits StanceResyncDelaySeconds, and that constant is the specified 1.5 s
            StringAssert.Contains(delayed, "chain.AddDelaySeconds(StanceResyncDelaySeconds);");
            StringAssert.Contains(PlayerLocationSource(), "private const double StanceResyncDelaySeconds = 1.5;");
        }

        [TestMethod]
        public void Wiring_ResyncStanceToSelf_WarnsOnCombatModeWithPeaceStance()
        {
            var resync = ExtractMember(PlayerLocationSource(), "public void ResyncStanceToSelf(string reason)");

            Assert.IsTrue(Regex.IsMatch(resync, @"if \(mode != CombatMode\.NonCombat && stance == MotionStance\.NonCombat\)\s*log\.Warn\("),
                "a combat mode with a peace stance must log [STANCE_RESYNC] at Warn");
        }

        [TestMethod]
        public void Wiring_ResyncStanceToSelf_ReadsServerStateAndDoesNotWriteIt()
        {
            var resync = ExtractMember(PlayerLocationSource(), "public void ResyncStanceToSelf(string reason)");

            StringAssert.Contains(resync, "CurrentMotionState.Stance");
            StringAssert.Contains(resync, "ResendStanceToSelf(this, mode, stance, true);");
            StringAssert.Contains(resync, "[STANCE_RESYNC]");
            Assert.IsFalse(resync.Contains("NextUseTime"), "must not touch NextUseTime");
            Assert.IsFalse(resync.Contains("EnqueueBroadcast"), "must not broadcast to observers");
        }

        private static string PlayerLocationSource()
        {
            return File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Location.cs")));
        }

        private static string PlayerTickSource()
        {
            return File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Tick.cs")));
        }

        // ---- helpers ----

        private static string ExtractMember(string source, string signatureStart)
        {
            var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find '{signatureStart}'");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after '{signatureStart}'");
            return null;
        }

        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
