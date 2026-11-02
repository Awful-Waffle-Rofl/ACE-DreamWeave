using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    [TestClass]
    public class PinnacleEntryRulesTests
    {
        private static PinnacleEntryAction Decide(bool exists, PinnacleFightState s, bool awaiting, bool copyLive, bool sameKnot, out string refusal)
            => PinnacleEntryRules.Decide(exists, s, awaiting, false, copyLive, sameKnot, out refusal);

        private static PinnacleEntryAction DecideCollecting(bool copyLive, bool sameKnot, out string refusal)
            => PinnacleEntryRules.Decide(true, PinnacleFightState.Won, false, true, copyLive, sameKnot, out refusal);

        [TestMethod]
        public void WonCollecting_SameKnot_ReturnToCollect()
        {
            Assert.AreEqual(PinnacleEntryAction.ReturnToCollect, DecideCollecting(true, true, out var refusal));
            Assert.IsNull(refusal);
        }

        [TestMethod]
        public void WonCollecting_OtherKnot_Refused()
        {
            Assert.AreEqual(PinnacleEntryAction.Refuse, DecideCollecting(true, false, out var refusal));
            Assert.AreEqual(PinnacleEntryRules.OtherKnotRefusal, refusal);
        }

        [TestMethod]
        public void WonCollecting_DeadCopy_CreateCopy()
            => Assert.AreEqual(PinnacleEntryAction.CreateCopy, DecideCollecting(false, true, out _));

        [TestMethod]
        public void NoFight_CreateCopy()
        {
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(false, PinnacleFightState.NotStarted, false, false, false, out var refusal));
            Assert.IsNull(refusal);
        }

        [TestMethod]
        public void NotStartedLive_ReuseArrival()
            => Assert.AreEqual(PinnacleEntryAction.ReuseArrival, Decide(true, PinnacleFightState.NotStarted, false, true, true, out _));

        [TestMethod]
        public void NotStartedLive_OtherKnot_CreatesACopy()
        {
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(true, PinnacleFightState.NotStarted, false, true, false, out var refusal));
            Assert.IsNull(refusal);
        }

        [TestMethod]
        public void Held_SameKnot_Resume()
            => Assert.AreEqual(PinnacleEntryAction.ResumeHeld, Decide(true, PinnacleFightState.Held, false, true, true, out _));

        [TestMethod]
        public void Running_SameKnot_Resume()
            => Assert.AreEqual(PinnacleEntryAction.ResumeHeld, Decide(true, PinnacleFightState.Running, false, true, true, out _));

        [TestMethod]
        public void Held_OtherKnot_Refused()
        {
            Assert.AreEqual(PinnacleEntryAction.Refuse, Decide(true, PinnacleFightState.Held, false, true, false, out var refusal));
            Assert.IsFalse(string.IsNullOrEmpty(refusal));
        }

        [TestMethod]
        public void WonAwaiting_SameKnot_ReturnsFree()
            => Assert.AreEqual(PinnacleEntryAction.ReturnToCollect, Decide(true, PinnacleFightState.Won, true, true, true, out _), "owner ruling 2026-10-19: a won return is free");

        [TestMethod]
        public void WonAwaiting_OtherKnot_Refused()
            => Assert.AreEqual(PinnacleEntryAction.Refuse, Decide(true, PinnacleFightState.Won, true, true, false, out _));

        [TestMethod]
        public void DeadCopy_CreateCopy()
        {
            foreach (var s in new[] { PinnacleFightState.NotStarted, PinnacleFightState.Running, PinnacleFightState.Held })
                Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(true, s, false, false, true, out _), s.ToString());
        }

        [TestMethod]
        public void EndedFight_CreateCopy()
        {
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(true, PinnacleFightState.Lost, false, true, true, out _));
            Assert.AreEqual(PinnacleEntryAction.CreateCopy, Decide(true, PinnacleFightState.Won, false, true, true, out _));
        }

        [TestMethod]
        public void Refusal_IsAsciiAndShortEnoughForOneChatLine()
        {
            Assert.IsTrue(PinnacleEntryRules.OtherKnotRefusal.Length <= 250);

            foreach (var ch in PinnacleEntryRules.OtherKnotRefusal)
                Assert.IsTrue(ch < 128);
        }
    }
}
