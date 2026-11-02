using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Owner ruling 2026-10-15: one Thread clear replaces the level and PK facet requirements for PvP Arenas and
    /// Battlegrounds. The eligibility predicate, the one-time notice gate, and the wiring that makes admission and the
    /// notice read the same predicate. Pure: no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class PvpEligibilityTests
    {
        // ---------------- the predicate ----------------

        [TestMethod]
        public void HasClearedAThread_NothingCleared_IsFalse()
        {
            Assert.IsFalse(PvpEligibility.HasClearedAThread(0, 0, false));
        }

        /// <summary>Each of the three inputs, alone, flips the predicate; nothing else is needed.</summary>
        [TestMethod]
        public void HasClearedAThread_EachInputAlone_Flips()
        {
            Assert.IsTrue(PvpEligibility.HasClearedAThread(1, 0, false), "a counted Thread clear");
            Assert.IsTrue(PvpEligibility.HasClearedAThread(0, 1, false), "a Thread-Guide rung won (the level-50 intro Thread)");
            Assert.IsTrue(PvpEligibility.HasClearedAThread(0, 0, true), "a veteran with survey history but ThreadClearsLifetime 0");
        }

        [TestMethod]
        public void HasClearedAThread_NegativeCounts_AreNotClears()
        {
            Assert.IsFalse(PvpEligibility.HasClearedAThread(-1, -1, false));
        }

        // ---------------- the notice gate ----------------

        [TestMethod]
        public void ShouldSendNotice_Table()
        {
            Assert.IsTrue(PvpEligibility.ShouldSendNotice(arenaEnabled: true, noticeAlreadyShown: false, hasClearedThread: true), "eligible, arena on, unlatched");
            Assert.IsFalse(PvpEligibility.ShouldSendNotice(false, false, true), "arena off");
            Assert.IsFalse(PvpEligibility.ShouldSendNotice(true, true, true), "already latched");
            Assert.IsFalse(PvpEligibility.ShouldSendNotice(true, false, false), "no Thread cleared");
        }

        [TestMethod]
        public void SendNoticeIfDue_ArenaOff_NeitherLatchesNorSends()
        {
            var log = new List<string>();

            var fired = PvpEligibility.SendNoticeIfDue(false, false, true, () => log.Add("latch"), () => log.Add("send"));

            Assert.IsFalse(fired);
            Assert.AreEqual(0, log.Count, "the arena being off must leave the notice unspent so it fires once the arena opens");
        }

        [TestMethod]
        public void SendNoticeIfDue_AlreadyLatched_DoesNothing()
        {
            var log = new List<string>();

            Assert.IsFalse(PvpEligibility.SendNoticeIfDue(true, true, true, () => log.Add("latch"), () => log.Add("send")));
            Assert.AreEqual(0, log.Count);
        }

        [TestMethod]
        public void SendNoticeIfDue_NotEligible_DoesNothing()
        {
            var log = new List<string>();

            Assert.IsFalse(PvpEligibility.SendNoticeIfDue(true, false, false, () => log.Add("latch"), () => log.Add("send")));
            Assert.AreEqual(0, log.Count);
        }

        /// <summary>DISCRIMINATES the ordering: the latch runs BEFORE the send, so a crash between them cannot repeat the notice.</summary>
        [TestMethod]
        public void SendNoticeIfDue_Eligible_LatchesBeforeSending()
        {
            var log = new List<string>();

            var fired = PvpEligibility.SendNoticeIfDue(true, false, true, () => log.Add("latch"), () => log.Add("send"));

            Assert.IsTrue(fired);
            CollectionAssert.AreEqual(new[] { "latch", "send" }, log);
        }

        // ---------------- the text, pinned ----------------

        [TestMethod]
        public void Texts_AreTheOwnersWords()
        {
            Assert.AreEqual("You have completed a Thread and are now eligible to join PvP Arenas and Battlegrounds! Visit the PK room on the lower level of the Marketplace for more details.", PvpEligibility.NoticeText);
            Assert.AreEqual("[Arena] You must complete at least one Thread before you can join PvP Arenas or Battlegrounds. The Thread-Guide's first Thread counts.", PvpArenaText.NoThreadCleared);
        }

        // ---------------- wiring (source scans bound to the identifier) ----------------

        /// <summary>
        /// Admission and the notice must read ONE predicate: Player.HasClearedAThread calls PvpEligibility.HasClearedAThread,
        /// the arena facts take it from that Player property, and the notice gates on the same property.
        /// </summary>
        [TestMethod]
        public void AdmissionFacts_AndNotice_ShareOnePredicate()
        {
            var player = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_PvpEligibility.cs");
            StringAssert.Contains(player, "public bool HasClearedAThread => PvpEligibility.HasClearedAThread(");

            var notice = PooledLootSourceText.MethodBody(player, "public void SendPvpEligibleNoticeIfDue()");
            StringAssert.Contains(notice, "PvpTunables.DialSource().Enabled");
            StringAssert.Contains(notice, "HasClearedAThread,");

            // The per-player short-circuit and the no-session guard both precede the arena dial read and the latch.
            var early = notice.IndexOf("if (PvpEligibleNoticeShown || !HasClearedAThread)", System.StringComparison.Ordinal);
            var noSession = notice.IndexOf("if (Session?.Network == null)", System.StringComparison.Ordinal);
            var dial = notice.IndexOf("PvpTunables.DialSource()", System.StringComparison.Ordinal);
            var latchAt = notice.IndexOf("PvpEligibleNoticeShown = true;", System.StringComparison.Ordinal);
            Assert.IsTrue(early >= 0 && noSession > early && dial > noSession && latchAt > dial, $"early={early} noSession={noSession} dial={dial} latch={latchAt}");

            var latch = notice.IndexOf("PvpEligibleNoticeShown = true;", System.StringComparison.Ordinal);
            var save = notice.IndexOf("SaveBiotaToDatabase();", System.StringComparison.Ordinal);
            var popup = notice.IndexOf("GameEventPopupString", System.StringComparison.Ordinal);
            var chat = notice.IndexOf("GameMessageSystemChat", System.StringComparison.Ordinal);
            Assert.IsTrue(latch > 0 && save > latch && popup > save && chat > save, $"latch={latch} save={save} popup={popup} chat={chat}");

            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/Pvp/PvpLiveAdapters.cs"), "HasClearedThread: p.HasClearedAThread");
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/Pvp/PvpMatchCoordinator.cs"), "HasClearedThread: facts.HasClearedThread,");
        }

        /// <summary>The notice is told right after the clear is counted, on the player's own queue, and again on the login catch-up.</summary>
        [TestMethod]
        public void Notice_IsHookedAfterTheClearCount_AndOnLogin()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs"),
                "public static void IncrementClears(Player player)");

            var increment = body.IndexOf("player.ThreadClearsLifetime = player.ThreadClearsLifetime + 1;", System.StringComparison.Ordinal);
            var notice = body.IndexOf("player.SendPvpEligibleNoticeIfDue();", System.StringComparison.Ordinal);
            Assert.IsTrue(increment > 0 && notice > increment, $"increment={increment} notice={notice}");

            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Networking.cs"), "SendPvpEligibleNoticeIfDue();");
        }
    }
}
