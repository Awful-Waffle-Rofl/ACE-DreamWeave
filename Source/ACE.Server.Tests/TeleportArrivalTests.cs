using System;
using System.IO;
using System.Text.RegularExpressions;

using ACE.Server.Entity;
using ACE.Server.Network.Enum;
using ACE.Server.Network.Structure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The server-side arrival fallback (Entity/TeleportArrival.cs, WorldObjects/Player_TeleportArrival.cs): the pure
    /// movement and timer rules, the once-per-window latch, and source pins for where the fallback is wired in (the
    /// wiring needs a live Player, so it is asserted on the source, in the TeleportWatchWiringTests style).
    /// </summary>
    [TestClass]
    public class TeleportArrivalTests
    {
        private const uint DestCell = 0x01F5011Eu;      // landblock 0x01F5
        private const uint SameBlockCell = 0x01F50003u; // another cell of 0x01F5
        private const uint OtherBlockCell = 0xA9B4003Fu;

        private static bool Movement(bool enabled = true, bool teleporting = true, bool loggingOut = false, bool materialized = false,
            double elapsed = 5.0, ArrivalInput input = ArrivalInput.MoveToState, bool hasMovement = true, uint reported = DestCell, uint dest = DestCell)
            => TeleportArrivalRules.ShouldCompleteOnMovement(enabled, teleporting, loggingOut, materialized, elapsed, input, hasMovement, reported, dest);

        private static MoveToState Report(uint cell, RawMotionFlags flags, ushort teleportSequence = 0)
        {
            return new MoveToState
            {
                RawMotionState = new RawMotionState { Flags = flags },
                // relativePos: keep the cell as given (otherwise an outdoor cell is recomputed from x/y)
                Position = new ACE.Entity.Position(cell, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0, true),
                TeleportSequence = teleportSequence,
            };
        }

        // ---- movement trigger ----

        [TestMethod]
        public void Movement_MoveToStateInDestinationLandblock_Triggers()
        {
            Assert.IsTrue(Movement());
            Assert.IsTrue(Movement(reported: SameBlockCell), "any cell of the destination landblock counts");
        }

        [TestMethod]
        public void Movement_MoveToStateInAnotherLandblock_DoesNotTrigger()
        {
            Assert.IsFalse(Movement(reported: OtherBlockCell));
        }

        [TestMethod]
        public void Movement_BeforeTheGraceWindow_DoesNotTrigger()
        {
            Assert.IsFalse(Movement(elapsed: 1.99));
            Assert.IsFalse(Movement(elapsed: 0));
            Assert.IsTrue(Movement(elapsed: TeleportArrivalRules.MovementGraceSeconds), "the window opens at exactly the grace");
            Assert.AreEqual(2.0, TeleportArrivalRules.MovementGraceSeconds);
        }

        [TestMethod]
        public void Movement_AutonomousPositionNeverTriggers()
        {
            Assert.IsFalse(Movement(input: ArrivalInput.AutonomousPosition));
            Assert.IsFalse(Movement(input: ArrivalInput.AutonomousPosition, elapsed: 600));
        }

        [TestMethod]
        public void Movement_JumpDoesNotTrigger_ItCarriesNoPosition()
        {
            Assert.IsFalse(Movement(input: ArrivalInput.Jump));
        }

        [TestMethod]
        public void Movement_WithoutMovementCommands_DoesNotTrigger()
        {
            Assert.IsFalse(Movement(hasMovement: false));
        }

        [TestMethod]
        public void Movement_Gates()
        {
            Assert.IsFalse(Movement(enabled: false), "the kill switch");
            Assert.IsFalse(Movement(teleporting: false), "nothing pending");
            Assert.IsFalse(Movement(loggingOut: true), "logging out");
            Assert.IsFalse(Movement(materialized: true), "this window already materialized");
            Assert.IsFalse(Movement(reported: 0), "no reported cell");
            Assert.IsFalse(Movement(dest: 0), "no recorded destination for this window");
            Assert.IsFalse(Movement(reported: 0, dest: 0), "two unknown cells are not a match");
        }

        /// <summary>
        /// KNOWN GAP (see MovementGraceSeconds): past the grace window a stale report from before a teleport that stays inside
        /// one landblock is indistinguishable from a fresh one by cell. TeleportSequence would tell them apart; it is logged on
        /// the forced line but not enforced yet. Written as the DESIRED behaviour and ignored, so it never pins the gap as
        /// correct: remove the Ignore once the sequence is enforced and the trigger can see the report is stale.
        /// </summary>
        [TestMethod]
        [Ignore("flip when TeleportSequence is enforced")]
        public void Movement_StaleSameLandblockReportPastTheGrace_DoesNotTrigger()
        {
            var origin = 0x01F50005u;   // the teleport started in the destination's own landblock
            Assert.IsFalse(Movement(reported: origin, dest: DestCell, elapsed: 2.5));
        }

        // ---- movement argument derivation ----

        [TestMethod]
        public void Derive_HasMovement_FollowsTheMovementCommandFlags()
        {
            Assert.IsTrue(TeleportArrivalRules.DeriveMovementArgs(Report(DestCell, RawMotionFlags.ForwardCommand), 100, 105, true, DestCell).HasMovement);
            Assert.IsTrue(TeleportArrivalRules.DeriveMovementArgs(Report(DestCell, RawMotionFlags.TurnCommand), 100, 105, true, DestCell).HasMovement);
            Assert.IsTrue(TeleportArrivalRules.DeriveMovementArgs(Report(DestCell, RawMotionFlags.SideStepCommand), 100, 105, true, DestCell).HasMovement);
            Assert.IsFalse(TeleportArrivalRules.DeriveMovementArgs(Report(DestCell, RawMotionFlags.CurrentStyle), 100, 105, true, DestCell).HasMovement,
                "a stance-only report (key release, style change) is not movement");

            var noMotion = Report(DestCell, RawMotionFlags.ForwardCommand);
            noMotion.RawMotionState = null;
            Assert.IsFalse(TeleportArrivalRules.DeriveMovementArgs(noMotion, 100, 105, true, DestCell).HasMovement);
            Assert.IsFalse(TeleportArrivalRules.DeriveMovementArgs(null, 100, 105, true, DestCell).HasMovement);
        }

        [TestMethod]
        public void Derive_Grace_IsMeasuredFromTheWindowStart()
        {
            var args = TeleportArrivalRules.DeriveMovementArgs(Report(DestCell, RawMotionFlags.ForwardCommand), 1000.0, 1001.5, true, DestCell);
            Assert.AreEqual(1.5, args.ElapsedSeconds, 1e-9);
            Assert.IsFalse(Movement(elapsed: args.ElapsedSeconds));
        }

        [TestMethod]
        public void Derive_Cells_ReportFromThePacket_DestinationFromTheRecordedWindow()
        {
            var args = TeleportArrivalRules.DeriveMovementArgs(Report(SameBlockCell, RawMotionFlags.ForwardCommand, 7), 100, 105, true, DestCell);
            Assert.AreEqual(SameBlockCell, args.ReportedCell);
            Assert.AreEqual(DestCell, args.DestinationCell);
            Assert.AreEqual((ushort)7, args.ReportedTeleportSequence);

            var stale = TeleportArrivalRules.DeriveMovementArgs(Report(SameBlockCell, RawMotionFlags.ForwardCommand), 100, 105, false, DestCell);
            Assert.AreEqual(0u, stale.DestinationCell, "a watch record that is not this window's gives no destination");

            var noPos = Report(DestCell, RawMotionFlags.ForwardCommand);
            noPos.Position = null;
            Assert.AreEqual(0u, TeleportArrivalRules.DeriveMovementArgs(noPos, 100, 105, true, DestCell).ReportedCell);
        }

        /// <summary>
        /// When Teleport()'s UpdatePlayerPosition fails, Location can still be the ORIGIN. The trigger compares against the
        /// recorded intended destination, so a report from the origin cannot complete it and one from the destination can.
        /// </summary>
        [TestMethod]
        public void Derive_LocationStillAtOrigin_OnlyAReportFromTheIntendedDestinationTriggers()
        {
            const uint originCell = OtherBlockCell;   // where Location still points
            const uint intended = DestCell;           // TeleportWatch.DestCell

            bool Triggers(uint reported)
            {
                var a = TeleportArrivalRules.DeriveMovementArgs(Report(reported, RawMotionFlags.ForwardCommand), 100, 110, true, intended);
                return TeleportArrivalRules.ShouldCompleteOnMovement(true, true, false, false, a.ElapsedSeconds, ArrivalInput.MoveToState,
                    a.HasMovement, a.ReportedCell, a.DestinationCell);
            }

            Assert.IsFalse(Triggers(originCell), "a stale report from the origin must not complete the arrival");
            Assert.IsTrue(Triggers(SameBlockCell), "a report from the intended destination completes it");
        }

        [TestMethod]
        public void SequenceFields_ReportEchoAndMismatch()
        {
            Assert.AreEqual("teleSeq=5 selfSeq=none liveSeq=5 seqMatch=true", TeleportArrivalRules.FormatSequenceFields(5, null, 5, out var m1));
            Assert.IsFalse(m1);

            Assert.AreEqual("teleSeq=4 selfSeq=none liveSeq=5 seqMatch=false", TeleportArrivalRules.FormatSequenceFields(4, null, 5, out var m2));
            Assert.IsTrue(m2);

            TeleportArrivalRules.FormatSequenceFields(3, 3, 9, out var m3);
            Assert.IsFalse(m3, "a pinned SelfTeleportSequence is what this client was sent, not the live one");

            Assert.AreEqual("teleSeq=3 selfSeq=none liveSeq=none seqMatch=n/a", TeleportArrivalRules.FormatSequenceFields(3, null, null, out var m4));
            Assert.IsFalse(m4);
        }

        // ---- timer backstop ----

        private static ForcedCompleteTimerDecision Timer(long limit = 30, bool teleporting = true, bool materialized = false, bool connected = true,
            bool loggingOut = false, double? scheduled = 1000.0, double? current = 1000.0, double elapsed = 30.0)
            => TeleportArrivalRules.EvaluateTimer(limit, teleporting, materialized, connected, loggingOut, scheduled, current, elapsed, out _);

        [TestMethod]
        public void Timer_FiresAt30_NotAt29()
        {
            Assert.AreEqual(ForcedCompleteTimerDecision.Fire, Timer(elapsed: 30.0));
            Assert.AreEqual(ForcedCompleteTimerDecision.Fire, Timer(elapsed: 31.5));

            var at29 = TeleportArrivalRules.EvaluateTimer(30, true, false, true, false, 1000.0, 1000.0, 29.0, out var remaining);
            Assert.AreEqual(ForcedCompleteTimerDecision.Reschedule, at29);
            Assert.AreEqual(1.0, remaining, 1e-9);
        }

        [TestMethod]
        public void Timer_ZeroDisables()
        {
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(limit: 0));
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(limit: 0, elapsed: 9999));
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(limit: -5));
        }

        [TestMethod]
        public void Timer_SupersededTeleportIsSkipped()
        {
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(scheduled: 1000.0, current: 1012.5));
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(scheduled: null, current: 1000.0));
        }

        [TestMethod]
        public void Timer_Gates()
        {
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(teleporting: false), "already arrived");
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(materialized: true), "this window already materialized");
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(connected: false), "only while the session is still connected");
            Assert.AreEqual(ForcedCompleteTimerDecision.Skip, Timer(loggingOut: true), "not while logging out");
        }

        [TestMethod]
        public void Timer_RescheduleNeverSpins()
        {
            var decision = TeleportArrivalRules.EvaluateTimer(30, true, false, true, false, 1000.0, 1000.0, 29.9999, out var remaining);

            Assert.AreEqual(ForcedCompleteTimerDecision.Reschedule, decision);
            Assert.AreEqual(TeleportArrivalRules.MinRescheduleSeconds, remaining);
        }

        // ---- first-login block ----

        [TestMethod]
        public void FirstEnterWorld_BySourceAndKind()
        {
            var kinds = new TeleportKind?[] { TeleportKind.Login, TeleportKind.Portal, TeleportKind.Teleport, null };

            foreach (var kind in kinds)
            {
                Assert.IsFalse(TeleportArrivalRules.RunsFirstEnterWorld(TeleportCompletionSource.Timer, kind),
                    $"a timer proves nothing about the client (kind {kind?.ToString() ?? "unknown"}); FirstEnterWorldDone gates SetCharacterOptions and DDD");
                Assert.IsTrue(TeleportArrivalRules.RunsFirstEnterWorld(TeleportCompletionSource.Client, kind),
                    $"the client's own LoginComplete always may (kind {kind?.ToString() ?? "unknown"})");
            }

            Assert.IsFalse(TeleportArrivalRules.RunsFirstEnterWorld(TeleportCompletionSource.Movement, TeleportKind.Login),
                "at login only the client's LoginComplete runs the first-login handshake");
            Assert.IsFalse(TeleportArrivalRules.RunsFirstEnterWorld(TeleportCompletionSource.Movement, null),
                "an unknown kind is treated like a login");
            Assert.IsTrue(TeleportArrivalRules.RunsFirstEnterWorld(TeleportCompletionSource.Movement, TeleportKind.Portal));
            Assert.IsTrue(TeleportArrivalRules.RunsFirstEnterWorld(TeleportCompletionSource.Movement, TeleportKind.Teleport));
        }

        // ---- one retry chain per window ----

        [TestMethod]
        public void ChainGate_ManyReDrivesDuringTheWait_LeaveAtMostOneLiveChain_AndMaterializeOnce()
        {
            var gate = new PreMaterializeChainGate();
            var latch = new TeleportArrivalLatch();
            const double stamp = 1000.0;
            const ulong lb = 0x01F5FFFFul;

            // model of OnTeleportComplete during the wait: claim, or return because a chain is already live
            var chainsLive = 0;
            void Enter()
            {
                if (latch.IsMaterialized(stamp))
                    return;
                if (gate.TryClaim(stamp, lb))
                    chainsLive++;
            }

            Enter();                      // the first source starts the wait
            for (var i = 0; i < 25; i++)   // a burst of re-drives (MoveToStates, the timer, a late LoginComplete)
                Enter();

            Assert.AreEqual(1, chainsLive, "re-drives during the wait must not start extra retry chains");
            Assert.IsTrue(gate.IsLive(stamp, lb));

            // the chain fires: it releases, then re-enters; a re-drive landing in between takes the claim instead
            gate.Release(stamp, lb);
            chainsLive--;
            Enter();                      // the re-drive that lands first schedules the successor
            Enter();                      // the chain's own re-entry then returns
            Assert.AreEqual(1, chainsLive, "still exactly one chain after a hand-over");

            // the landblock finishes: the next pass ends the wait and materializes, once
            gate.Release(stamp, lb);
            chainsLive--;
            gate.Clear();
            Assert.IsTrue(latch.TryMaterialize(stamp, out _));
            Assert.IsFalse(latch.TryMaterialize(stamp, out _));
            Assert.IsFalse(gate.IsLive(stamp, lb));
            Assert.AreEqual(0, chainsLive);
        }

        [TestMethod]
        public void ChainGate_IsKeyedOnTheWindowAndLandblock()
        {
            var gate = new PreMaterializeChainGate();

            Assert.IsTrue(gate.TryClaim(1000.0, 1));
            Assert.IsFalse(gate.TryClaim(1000.0, 1));
            Assert.IsTrue(gate.TryClaim(1012.0, 1), "a newer window is never blocked by an older claim");

            gate.Release(1000.0, 1);
            Assert.IsTrue(gate.IsLive(1012.0, 1), "an older window's chain must not release a newer window's claim");
            Assert.IsFalse(gate.TryClaim(1012.0, 1));

            gate.Release(1012.0, 1);
            Assert.IsTrue(gate.TryClaim(1012.0, 1));
        }

        [TestMethod]
        public void Wiring_RetryChain_IsGatedAndReleasesBeforeReEntering()
        {
            var body = ExtractMember(WorldObjectSource("Player_Location.cs"), "public void OnTeleportComplete()");

            var bound = body.IndexOf("if (waited < MaxPreMaterializeWait)", StringComparison.Ordinal);
            var claim = body.IndexOf("if (!preMaterializeChainGate.TryClaim(waitStamp, waitLongId))", StringComparison.Ordinal);
            var release = body.IndexOf("actionChain.AddAction(this, () => preMaterializeChainGate.Release(waitStamp, waitLongId));", StringComparison.Ordinal);
            var retry = body.IndexOf("actionChain.AddAction(this, OnTeleportComplete);", StringComparison.Ordinal);
            var clear = body.IndexOf("preMaterializeChainGate.Clear();", StringComparison.Ordinal);
            var materialize = body.IndexOf("TryMaterializeArrival(out var arrivalSource)", StringComparison.Ordinal);

            Assert.IsTrue(bound >= 0 && claim > bound && claim < retry, "a chain is only scheduled after the claim succeeds");
            Assert.IsTrue(release > claim && release < retry, "the chain releases its claim as the step before it re-enters");
            Assert.IsTrue(clear > retry && clear < materialize, "the claim is cleared where the wait ends");
        }

        // ---- once per window ----

        [TestMethod]
        public void Latch_LateClientLoginCompleteAfterForcedCompletion_DoesNotDoubleRun()
        {
            var latch = new TeleportArrivalLatch();
            const double stamp = 1000.0;

            // the timer forces it: it owns the window and materializes
            latch.NoteRequest(stamp, TeleportCompletionSource.Timer);
            Assert.IsTrue(latch.TryMaterialize(stamp, out var source));
            Assert.AreEqual(TeleportCompletionSource.Timer, source);

            // the real LoginComplete arrives late: CompleteArrival sees the window materialized and does not re-enter
            latch.NoteRequest(stamp, TeleportCompletionSource.Client);
            Assert.IsTrue(latch.IsMaterialized(stamp), "CompleteArrival skips OnTeleportComplete for a materialized window");
            Assert.IsFalse(latch.TryMaterialize(stamp, out _), "OnTeleportComplete's own guard refuses the window a second time");
            Assert.IsTrue(latch.TryNoteLateClient(stamp));
            Assert.IsFalse(latch.TryNoteLateClient(stamp), "one late-client note per window");
            Assert.AreEqual(TeleportCompletionSource.Timer, latch.SourceFor(stamp), "a later source does not rewrite the attribution");
        }

        [TestMethod]
        public void Latch_RequestedButUnmaterializedWindow_CanBeReDrivenByTheTimer_AndMaterializesOnce()
        {
            var latch = new TeleportArrivalLatch();
            const double stamp = 1000.0;

            // the client asked, but its retry chain never got the player materialized
            latch.NoteRequest(stamp, TeleportCompletionSource.Client);
            Assert.IsTrue(latch.IsRequested(stamp));
            Assert.IsFalse(latch.IsMaterialized(stamp));

            // the request does not block the backstop
            Assert.AreEqual(ForcedCompleteTimerDecision.Fire,
                TeleportArrivalRules.EvaluateTimer(30, true, latch.IsMaterialized(stamp), true, false, stamp, stamp, 30, out _));
            Assert.IsTrue(Movement(materialized: latch.IsMaterialized(stamp)), "nor the movement trigger");

            // the re-driven call and any surviving retry chain race to materialize: exactly one wins
            Assert.IsTrue(latch.TryMaterialize(stamp, out _));
            Assert.IsFalse(latch.TryMaterialize(stamp, out _));
            Assert.IsFalse(latch.TryMaterialize(stamp, out _));

            Assert.AreEqual(ForcedCompleteTimerDecision.Skip,
                TeleportArrivalRules.EvaluateTimer(30, true, latch.IsMaterialized(stamp), true, false, stamp, stamp, 60, out _));
        }

        [TestMethod]
        public void Latch_ForcedTelemetryOncePerWindowPerSource()
        {
            var latch = new TeleportArrivalLatch();

            Assert.IsTrue(latch.TryNoteForced(1000.0, TeleportCompletionSource.Movement));
            Assert.IsFalse(latch.TryNoteForced(1000.0, TeleportCompletionSource.Movement), "a second MoveToState during the landblock wait is not a new decision");
            Assert.IsTrue(latch.TryNoteForced(1000.0, TeleportCompletionSource.Timer), "a different source is its own decision");
            Assert.IsFalse(latch.TryNoteForced(1000.0, TeleportCompletionSource.Client), "the client is never a forced decision");
            Assert.IsTrue(latch.TryNoteForced(1012.0, TeleportCompletionSource.Movement), "a new window re-arms");
        }

        [TestMethod]
        public void Latch_ClientDoubleAck_IsADuplicateButNotALateClient()
        {
            var latch = new TeleportArrivalLatch();
            const double stamp = 1000.0;

            latch.NoteRequest(stamp, TeleportCompletionSource.Client);
            Assert.IsTrue(latch.TryMaterialize(stamp, out _));
            latch.NoteRequest(stamp, TeleportCompletionSource.Client);
            Assert.IsTrue(latch.IsMaterialized(stamp));
            Assert.IsFalse(latch.TryNoteLateClient(stamp), "only a client ack after a FORCED completion is a late client");
        }

        [TestMethod]
        public void Latch_NewWindowRearms()
        {
            var latch = new TeleportArrivalLatch();

            latch.NoteRequest(1000.0, TeleportCompletionSource.Movement);
            Assert.IsTrue(latch.TryMaterialize(1000.0, out _));

            Assert.IsFalse(latch.IsRequested(1012.0));
            Assert.IsFalse(latch.IsMaterialized(1012.0));
            latch.NoteRequest(1012.0, TeleportCompletionSource.Client);
            Assert.IsTrue(latch.TryMaterialize(1012.0, out var source));
            Assert.AreEqual(TeleportCompletionSource.Client, source);
        }

        [TestMethod]
        public void Latch_UnrequestedWindowMaterializesOnceAsClient()
        {
            // the pink-bubble retry chain re-enters OnTeleportComplete directly, without a request
            var latch = new TeleportArrivalLatch();

            Assert.IsTrue(latch.TryMaterialize(1000.0, out var source));
            Assert.AreEqual(TeleportCompletionSource.Client, source);
            Assert.IsFalse(latch.TryMaterialize(1000.0, out _));
        }

        [TestMethod]
        public void Latch_NullStampNeverLatches()
        {
            var latch = new TeleportArrivalLatch();

            latch.NoteRequest(null, TeleportCompletionSource.Client);
            Assert.IsTrue(latch.TryMaterialize(null, out _));
            Assert.IsTrue(latch.TryMaterialize(null, out _));
            Assert.IsFalse(latch.IsRequested(null));
            Assert.IsFalse(latch.IsMaterialized(null));
        }

        [TestMethod]
        public void SourceTags()
        {
            Assert.AreEqual("client", TeleportArrivalRules.SourceTag(TeleportCompletionSource.Client));
            Assert.AreEqual("movement", TeleportArrivalRules.SourceTag(TeleportCompletionSource.Movement));
            Assert.AreEqual("timer", TeleportArrivalRules.SourceTag(TeleportCompletionSource.Timer));
        }

        // ---- wiring pins ----

        [TestMethod]
        public void Wiring_LoginComplete_GoesThroughTheSharedEntryPoint()
        {
            var handler = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "Network", "GameAction", "Actions", "GameActionLoginComplete.cs")));

            StringAssert.Contains(handler, "CompleteArrival(ACE.Server.Entity.TeleportCompletionSource.Client);");
            Assert.IsFalse(Regex.IsMatch(handler, @"\.OnTeleportComplete\(\)"), "the handler must not call OnTeleportComplete around the latch");
            Assert.IsFalse(handler.Contains("SendPropertyUpdatesAndOverrides"), "the first-login block lives in CompleteArrival");
        }

        [TestMethod]
        public void Wiring_CompleteArrival_ReEntersUntilMaterialized_AndGatesTheFirstLoginBlockOnTheSource()
        {
            var body = ExtractMember(WorldObjectSource("Player_TeleportArrival.cs"), "public void CompleteArrival(TeleportCompletionSource source)");

            var gate = body.IndexOf("if (!arrivalLatch.IsMaterialized(stamp))", StringComparison.Ordinal);
            var complete = body.IndexOf("OnTeleportComplete();", StringComparison.Ordinal);
            var firstLogin = body.IndexOf("if (TeleportArrivalRules.RunsFirstEnterWorld(source, ArrivalKind(stamp)) && !FirstEnterWorldDone)", StringComparison.Ordinal);
            var setFlag = body.IndexOf("FirstEnterWorldDone = true;", StringComparison.Ordinal);
            var props = body.IndexOf("SendPropertyUpdatesAndOverrides();", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0 && complete > gate, "OnTeleportComplete is re-entered whenever the window is not materialized");
            Assert.IsFalse(body.Contains("TryRequest"), "a request must never refuse a source");
            Assert.IsTrue(firstLogin > complete && setFlag > firstLogin && props > setFlag, "the first-login block is gated on the source");
            Assert.AreEqual(1, Regex.Matches(body, @"FirstEnterWorldDone = true;").Count, "the only write of the flag is the gated one");
        }

        [TestMethod]
        public void Wiring_OnTeleportComplete_LatchesBeforeAnySideEffect()
        {
            var body = ExtractMember(WorldObjectSource("Player_Location.cs"), "public void OnTeleportComplete()");

            var top = body.IndexOf("if (ArrivalAlreadyMaterialized())", StringComparison.Ordinal);
            var retry = body.IndexOf("AddAction(this, OnTeleportComplete)", StringComparison.Ordinal);
            var latch = body.IndexOf("TryMaterializeArrival(out var arrivalSource)", StringComparison.Ordinal);
            var unhide = body.IndexOf("Hidden = false;", StringComparison.Ordinal);
            var watch = body.IndexOf("TeleportWatchOnComplete();", StringComparison.Ordinal);
            var record = body.IndexOf("RecordArrivalCompleted(arrivalSource);", StringComparison.Ordinal);
            var cleared = body.IndexOf("Teleporting = false;", StringComparison.Ordinal);

            Assert.IsTrue(top >= 0 && top < retry, "an already-materialized window must stop before the landblock wait");
            Assert.IsTrue(latch > retry, "the materialize latch sits after the wait, where the player actually materializes");
            Assert.IsTrue(latch < unhide, "the latch must come before the first side effect");
            Assert.IsTrue(record > watch && record < cleared, "the duration is recorded on every completion, before Teleporting clears");
        }

        [TestMethod]
        public void Wiring_MoveToState_ChecksTheTriggerFirst()
        {
            var handler = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "Network", "GameAction", "Actions", "GameActionMoveToState.cs")));

            var parse = handler.IndexOf("new MoveToState(session.Player, message.Payload);", StringComparison.Ordinal);
            var check = handler.IndexOf("session.Player.CheckArrivalOnMovementInput(moveToState);", StringComparison.Ordinal);
            var onMove = handler.IndexOf("session.Player.OnMoveToState(moveToState);", StringComparison.Ordinal);
            var requested = handler.IndexOf("if (!session.Player.Teleporting)", StringComparison.Ordinal);

            Assert.IsTrue(parse >= 0 && check > parse, "the trigger needs the parsed report");
            Assert.IsTrue(check < onMove && check < requested, "completing first lets the rest of the handler treat the report as ordinary movement");
        }

        [TestMethod]
        public void Wiring_MovementTrigger_ComparesAgainstTheRecordedDestination_NotLocation()
        {
            var body = ExtractMember(WorldObjectSource("Player_TeleportArrival.cs"), "public void CheckArrivalOnMovementInput(MoveToState moveToState)");

            StringAssert.Contains(body, "teleportWatch.IsCurrent(stamp), teleportWatch.DestCell");
            Assert.IsFalse(body.Contains("Location"), "Location can still be the origin when UpdatePlayerPosition returned false");
            StringAssert.Contains(body, "arrivalLatch.IsMaterialized(stamp)");
        }

        [TestMethod]
        public void Wiring_AutonomousPositionAndJump_AreNotHooked()
        {
            foreach (var file in new[] { "GameActionAutonomousPosition.cs", "GameActionJump.cs" })
            {
                var handler = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "Network", "GameAction", "Actions", file)));
                Assert.IsFalse(handler.Contains("CheckArrivalOnMovementInput"), $"{file} must not trigger the arrival");
                Assert.IsFalse(handler.Contains("CompleteArrival"), $"{file} must not trigger the arrival");
            }
        }

        [TestMethod]
        public void Wiring_BothWindowOpenersScheduleTheBackstop()
        {
            var teleport = ExtractMember(WorldObjectSource("Player_Location.cs"), "public void Teleport(Position _newPosition");
            var open = teleport.IndexOf("OpenTeleportWatch(", StringComparison.Ordinal);
            var schedule = teleport.IndexOf("ScheduleForcedArrivalBackstop();", StringComparison.Ordinal);
            Assert.IsTrue(open >= 0 && schedule > open, "Teleport() schedules after the stamp's window opens");

            var enter = ExtractMember(WorldObjectSource("Player_Networking.cs"), "public void PlayerEnterWorld()");
            var loginOpen = enter.IndexOf("OpenTeleportWatch(", StringComparison.Ordinal);
            var loginSchedule = enter.IndexOf("ScheduleForcedArrivalBackstop();", StringComparison.Ordinal);
            Assert.IsTrue(loginOpen >= 0 && loginSchedule > loginOpen, "a login gets the same backstop");
        }

        /// <summary>
        /// The backstop's survival while the player is between landblocks rests on this: the delayed action's actor is the
        /// Player, and Player.EnqueueAction pushes onto its own queue unconditionally instead of WorldObject's
        /// CurrentLandblock-null drop. If this override changes, the timer (and the pink-bubble retry chain) can be lost.
        /// </summary>
        [TestMethod]
        public void Wiring_PlayerEnqueueAction_NeverDropsWhenDetached()
        {
            var body = ExtractMember(WorldObjectSource("Player_Tick.cs"), "public override void EnqueueAction(IAction action)");
            var statements = body.Substring(body.IndexOf('{') + 1).Trim().TrimEnd('}').Trim();

            Assert.AreEqual("actionQueue.EnqueueAction(action);", statements, "Player.EnqueueAction must queue unconditionally");

            var timer = ExtractMember(WorldObjectSource("Player_TeleportArrival.cs"), "private void ScheduleForcedArrivalCheck(double? scheduledStamp, double delaySeconds)");
            StringAssert.Contains(timer, "chain.AddAction(this, () => OnForcedArrivalTimer(scheduledStamp));", "the timer's actor must be the Player");
        }

        // ---- helpers ----

        private static string WorldObjectSource(string fileName)
        {
            return File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", fileName)));
        }

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
