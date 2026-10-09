using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The decision logic behind the pending-teleport diagnostics (Player_TeleportWatch): once-per-threshold-per-teleport
    /// watchdog lines, kind classification from the stamps the teleport and login paths write, and the abandon latch
    /// that keeps ace.teleport.abandoned to one per teleport even though LogOut_Inner can run several times.
    /// </summary>
    [TestClass]
    public class TeleportWatchTests
    {
        private const double T0 = 1790000000.25;
        private const double T1 = 1790000100.50;

        // ---- thresholds ----

        [TestMethod]
        public void Thresholds_FireOncePerThreshold_ThenResetOnANewStamp()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Portal, false);

            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, T0, 5), "nothing before 10s");
            Assert.AreEqual(TeleportPendingThreshold.First, watch.CheckThresholds(true, T0, 10), "the 10s line fires at 10s");
            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, T0, 15), "and only once");
            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, T0, 25), "and still only once");
            Assert.AreEqual(TeleportPendingThreshold.Second, watch.CheckThresholds(true, T0, 30), "the 30s line fires at 30s");
            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, T0, 300), "and only once");

            // a new teleport is a new stamp: both thresholds re-arm with no explicit reset
            watch.Begin(T1, TeleportKind.Teleport, false);
            Assert.AreEqual(TeleportPendingThreshold.First, watch.CheckThresholds(true, T1, 11), "a new teleport re-arms the 10s line");
        }

        [TestMethod]
        public void Thresholds_FirstCheckPastBoth_ReturnsBoth_SoPending10NeverTrailsPending30()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Teleport, false);

            Assert.AreEqual(TeleportPendingThreshold.First | TeleportPendingThreshold.Second, watch.CheckThresholds(true, T0, 31));
            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, T0, 36));
        }

        [TestMethod]
        public void Thresholds_NotTeleportingOrNoStamp_NeverFire()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Teleport, false);

            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(false, T0, 60), "completed teleports never warn");
            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, null, 60), "no stamp, nothing to key on");
        }

        // ---- kind ----

        [TestMethod]
        public void ClassifyKind_UsesTheStampsTheTeleportAndLoginPathsWrite()
        {
            // Teleport(fromPortal: true) copies the start stamp into LastPortalTeleportTimestamp
            Assert.AreEqual(TeleportKind.Portal, TeleportWatchRules.ClassifyKind(T1, T1, T0));

            // PlayerEnterWorld stamps LoginTimestamp and LastTeleportStartTimestamp with one value; the portal stamp is
            // left over from an earlier session
            Assert.AreEqual(TeleportKind.Login, TeleportWatchRules.ClassifyKind(T1, T0, T1));

            // any other teleport (recall, /tele, lifestone) matches neither
            Assert.AreEqual(TeleportKind.Teleport, TeleportWatchRules.ClassifyKind(T1, T0, T0));
            Assert.AreEqual(TeleportKind.Teleport, TeleportWatchRules.ClassifyKind(T1, null, null));
            Assert.AreEqual(TeleportKind.Teleport, TeleportWatchRules.ClassifyKind(null, null, null));

            Assert.AreEqual("portal", TeleportWatchRules.KindTag(TeleportKind.Portal));
            Assert.AreEqual("login", TeleportWatchRules.KindTag(TeleportKind.Login));
            Assert.AreEqual("teleport", TeleportWatchRules.KindTag(TeleportKind.Teleport));
        }

        // ---- abandon ----

        [TestMethod]
        public void Abandon_StuckLogoffThenItsLogoutThenTheSessionDrop_CountsOnce_AsStuckLogoff()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Portal, false);

            // Heartbeat: MaximumTeleportTime passed, the stuck block notes it and forces the logoff
            watch.NoteStuckLogoff(T0);

            Assert.IsTrue(watch.TryAbandon(true, T0), "the first LogOut_Inner while pending abandons");
            Assert.AreEqual(TeleportAbandonReason.StuckLogoff, TeleportWatchRules.ClassifyAbandon(watch.StuckLogoffFired(T0), false));

            // the session-less stuck branch calls LogOut again on the next heartbeat, and the session drop follows
            Assert.IsFalse(watch.TryAbandon(true, T0), "a second logout for the same teleport is not a second abandon");
            Assert.IsFalse(watch.TryAbandon(true, T0), "nor a third");

            // a LoginComplete landing during the logout animation is not also a completion
            Assert.IsFalse(watch.TryComplete(true, T0), "an abandoned teleport does not also complete");
        }

        [TestMethod]
        public void Abandon_ReasonPrecedence_StuckBeatsDisconnectBeatsLogout()
        {
            Assert.AreEqual(TeleportAbandonReason.StuckLogoff, TeleportWatchRules.ClassifyAbandon(true, true));
            Assert.AreEqual(TeleportAbandonReason.Disconnect, TeleportWatchRules.ClassifyAbandon(false, true));
            Assert.AreEqual(TeleportAbandonReason.Logout, TeleportWatchRules.ClassifyAbandon(false, false));

            Assert.AreEqual("stuck-logoff", TeleportWatchRules.ReasonTag(TeleportAbandonReason.StuckLogoff));
            Assert.AreEqual("disconnect", TeleportWatchRules.ReasonTag(TeleportAbandonReason.Disconnect));
            Assert.AreEqual("logout", TeleportWatchRules.ReasonTag(TeleportAbandonReason.Logout));
        }

        [TestMethod]
        public void Abandon_StuckLogoffOfAnEarlierTeleport_DoesNotLabelALaterOne()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Teleport, false);
            watch.NoteStuckLogoff(T0);

            watch.Begin(T1, TeleportKind.Teleport, false);
            Assert.IsFalse(watch.StuckLogoffFired(T1));
        }

        [TestMethod]
        public void Abandon_NotTeleportingOrAlreadyCompleted_DoesNotCount()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Teleport, false);

            Assert.IsFalse(watch.TryAbandon(false, T0), "an ordinary logout after arrival is not an abandon");

            Assert.IsTrue(watch.TryComplete(true, T0));
            Assert.IsFalse(watch.TryComplete(true, T0), "completion counts once");
            Assert.IsFalse(watch.TryAbandon(true, T0), "a completed teleport cannot be abandoned");

            // the next teleport is a fresh window
            watch.Begin(T1, TeleportKind.Teleport, false);
            Assert.IsTrue(watch.TryAbandon(true, T1));
        }

        // ---- superseded ----

        [TestMethod]
        public void Begin_WhileStillPending_IsSuperseded_AndCountsTheChain()
        {
            var watch = new TeleportWatch();

            Assert.IsFalse(watch.Begin(T0 - 10, TeleportKind.Login, false), "the first window supersedes nothing");

            // Teleporting already false when the next one starts: that window is not superseded, whatever its stamps say
            Assert.IsFalse(watch.Begin(T0, TeleportKind.Login, false), "a start with Teleporting clear supersedes nothing");
            Assert.AreEqual(0, watch.SupersededCount);

            Assert.IsTrue(watch.Begin(T1, TeleportKind.Teleport, true), "a teleport while the login is pending supersedes it");
            Assert.AreEqual(1, watch.SupersededCount);
            Assert.IsTrue(watch.Begin(T1 + 1, TeleportKind.Teleport, true));
            Assert.AreEqual(2, watch.SupersededCount);

            // completed, then a new teleport: a clean start
            Assert.IsTrue(watch.TryComplete(true, T1 + 1));
            Assert.IsFalse(watch.Begin(T1 + 2, TeleportKind.Teleport, false));
            Assert.AreEqual(0, watch.SupersededCount);
        }

        [TestMethod]
        public void Begin_AfterAnAbandonedOrCompletedWindow_IsNotSuperseded_EvenWithTeleportingStillSet()
        {
            // abandoned: LogOut_Inner ran while pending, then a teleport starts before Teleporting is cleared
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Portal, false);
            Assert.IsTrue(watch.TryAbandon(true, T0));
            Assert.IsFalse(watch.Begin(T1, TeleportKind.Teleport, true), "an abandoned window was already accounted for");
            Assert.AreEqual(0, watch.SupersededCount);

            // completed: counted as completed, so a teleport started with Teleporting still set is not also superseded
            watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Portal, false);
            Assert.IsTrue(watch.TryComplete(true, T0));
            Assert.IsFalse(watch.Begin(T1, TeleportKind.Teleport, true), "a completed window was already accounted for");
            Assert.AreEqual(0, watch.SupersededCount);
        }

        // ---- end without a start ----

        [TestMethod]
        public void AStampThatNeverBegan_IsANoOp_ForEveryDecision()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Teleport, false);

            // T1 was stamped but its window never opened (the stamp and the open are separate statements)
            Assert.IsFalse(watch.IsCurrent(T1));
            Assert.IsFalse(watch.TryComplete(true, T1), "an end without a start is not a completion");
            Assert.IsFalse(watch.TryAbandon(true, T1), "nor an abandon");
            Assert.AreEqual(TeleportPendingThreshold.None, watch.CheckThresholds(true, T1, 60), "nor a pending line against T0's data");

            // T0's window is untouched by any of that
            Assert.IsTrue(watch.TryComplete(true, T0));
        }

        // ---- the line ----

        private static TeleportWatch PortalWatchWithBaseline()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Portal, false);
            watch.DestCell = 0x1B120033;
            watch.HasOrigin = true;
            watch.OriginCell = 0x01C901F1;
            watch.NetAtStart = new TeleportNetSnapshot { Available = true, PacketsReceived = 1000, CrcFailures = 3, GameActions = 50, ServerPacketSequence = 4000, RetransmitsServed = 2, ClientRetransmitRequests = 1 };
            watch.SelfPositionSendsAtStart = 40;
            watch.MarkBaseline();
            return watch;
        }

        private static TeleportNetSnapshot SnapshotAt(long now) => new TeleportNetSnapshot
        {
            Available = true,
            PacketsReceived = 1019,
            LastPacketReceivedTicks = now - TimeSpan.FromSeconds(0.4).Ticks,
            CrcFailures = 3,
            GameActions = 52,
            LastGameActionOpcode = 0x01E9,
            LastGameActionTicks = now - TimeSpan.FromSeconds(4.1).Ticks,
            RetransmitsServed = 2,
            ClientRetransmitRequests = 1,
            ServerPacketSequence = 4031,
            ClientAckSequence = 4029,
            ClientAckTicks = now - TimeSpan.FromSeconds(0.4).Ticks,
            OutboundQueueDepth = 0,
            RetainedPackets = 2,
        };

        [TestMethod]
        public void FormatFields_CarriesTheDeltasSinceTeleportStart()
        {
            var watch = PortalWatchWithBaseline();
            var now = new DateTime(2026, 10, 4, 22, 35, 59, DateTimeKind.Utc).Ticks;

            var line = TeleportWatchRules.FormatFields("Gabz", 0x50000ABC, watch, 12.3, 0x1B12, 0, true, SnapshotAt(now), "PingRequest", 41, now);

            Console.WriteLine("[TELEPORT_PENDING] threshold=10s " + line);

            StringAssert.StartsWith(line, "Gabz (0x50000ABC) kind=portal pending=12s from=0x01C901F1/0x00000000 to=0x1B120033/0x00000000 lb=0x1B12/0x00000000 lbLoaded=true");
            StringAssert.Contains(line, " inPkts=+19 inPktAgo=0.4s crcFail=+0/3 actions=+2 lastAction=PingRequest lastActionAgo=4.1s");
            StringAssert.Contains(line, " seqAtStart=4000 seqNow=4031 clientAck=4029 ackPastStart=+29 ackAgo=0.4s");
            StringAssert.Contains(line, " resends=+0/2 resendMiss=+0/0 clientNaks=+0/1 serverNaks=+0/0 outQueue=0 retained=2");
            StringAssert.EndsWith(line, " extraSelfPositions=+1 superseded=0");
            Assert.IsFalse(line.Contains("baseline=missing"), "the baseline ran");
            Assert.IsFalse(line.Contains("extraTeleportMsgs"), "removed: structurally always +0");
            Assert.IsFalse(line.Contains("\n"), "one line");
        }

        [TestMethod]
        public void FormatFields_BaselineNeverRan_SaysSo_AndDoesNotClaimAnOrigin()
        {
            // Teleport() opened the window and then threw before its baseline step: the fallback read from the open
            // step still carries the deltas
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Portal, false);
            watch.DestCell = 0x1B120033;
            watch.NetAtStart = new TeleportNetSnapshot { Available = true, PacketsReceived = 1000, ServerPacketSequence = 4000 };

            var now = new DateTime(2026, 10, 4, 22, 35, 59, DateTimeKind.Utc).Ticks;
            var line = TeleportWatchRules.FormatFields("Gabz", 0x50000ABC, watch, 12.3, 0x1B12, 0, true, SnapshotAt(now), "PingRequest", 41, now);

            Console.WriteLine("[TELEPORT_PENDING] threshold=10s " + line);

            StringAssert.Contains(line, "kind=portal pending=12s baseline=missing from=unknown to=0x1B120033/0x00000000");
            StringAssert.Contains(line, " inPkts=+19 ");
            Assert.IsFalse(line.Contains("net=unavailable"), "a missing baseline is not a missing network");
        }

        [TestMethod]
        public void FormatFields_NoNetworkOrLandblock_SaysSo()
        {
            var watch = new TeleportWatch();
            watch.Begin(T0, TeleportKind.Login, false);
            watch.DestCell = 0xA9B40019;
            watch.MarkBaseline();

            var line = TeleportWatchRules.FormatFields("Gabz", 0x50000ABC, watch, 101, null, null, null, default, null, 0, DateTime.UtcNow.Ticks);

            Console.WriteLine("[TELEPORT_ABANDONED] reason=disconnect " + line);

            StringAssert.Contains(line, "kind=login pending=101s from=none to=0xA9B40019/0x00000000 lb=none lbLoaded=n/a net=unavailable");
        }
    }
}
