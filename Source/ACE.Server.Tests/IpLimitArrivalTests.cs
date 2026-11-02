using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure arrival rule behind IpLimitManager.CheckOnArrival (immediate enforcement on teleport).
    /// maxFree 1, maxConfined 2 unless a test says otherwise.
    /// </summary>
    [TestClass]
    public class IpLimitArrivalTests
    {
        private const int Free = 1;
        private const int Confined = 2;

        private static IpLimitCandidate R(uint guid, int level, bool confined, double login = 100)
            => new IpLimitCandidate(guid, level, confined, login);

        private static readonly uint[] None = new uint[0];

        [TestMethod]
        public void Exploit_ResidentOutside_MoverArrivesOutside_Violates()
        {
            var residents = new List<IpLimitCandidate> { R(1, 200, false) };
            Assert.IsTrue(IpLimitManager.ArrivalViolates(false, residents, None, Free, Confined));
        }

        [TestMethod]
        public void MoverLosesEvenAgainstMuchLowerLevelResident()
        {
            // The helper hard-codes the mover to the lowest possible level, so the mover loses even against a
            // level 1 resident.
            var residents = new List<IpLimitCandidate> { R(1, 1, false) };
            Assert.IsTrue(IpLimitManager.ArrivalViolates(false, residents, None, Free, Confined));
        }

        [TestMethod]
        public void ResidentConfined_MoverArrivesOutside_Allowed()
        {
            var residents = new List<IpLimitCandidate> { R(1, 200, true) };
            Assert.IsFalse(IpLimitManager.ArrivalViolates(false, residents, None, Free, Confined));
        }

        [TestMethod]
        public void MoverArrivesConfined_OneOutsideResident_Allowed()
        {
            var residents = new List<IpLimitCandidate> { R(1, 200, false) };
            Assert.IsFalse(IpLimitManager.ArrivalViolates(true, residents, None, Free, Confined));
        }

        [TestMethod]
        public void TotalCap_ThreeConfinedResidents_MoverArrivesConfined_Violates()
        {
            var residents = new List<IpLimitCandidate> { R(1, 50, true), R(2, 60, true), R(3, 70, true) };
            Assert.IsTrue(IpLimitManager.ArrivalViolates(true, residents, None, Free, Confined));
        }

        [TestMethod]
        public void KnownGraceViolatorOutside_IsNotCounted_Allowed()
        {
            // Resident 1 is outside and already on a grace warning; resident 2 is confined. With 1 dropped the
            // mover is the only outside character (1 <= maxFree) and the total is 2 <= 3.
            var residents = new List<IpLimitCandidate> { R(1, 200, false), R(2, 100, true) };
            Assert.IsFalse(IpLimitManager.ArrivalViolates(false, residents, new uint[] { 1 }, Free, Confined));
        }

        [TestMethod]
        public void ResidentsAlreadyViolateAmongThemselves_SelfViolatorDropped_MoverJudgedAgainstRest()
        {
            // Residents 1 (L100) and 2 (L200) are both outside with no grace stamp; the rule alone picks the
            // lower level (1) as the violator, so it is dropped. The remainder is {2 outside}. The mover
            // arriving outside makes 2 outside against maxFree 1, and the mover always loses: true.
            var residents = new List<IpLimitCandidate> { R(1, 100, false), R(2, 200, false) };
            Assert.IsTrue(IpLimitManager.ArrivalViolates(false, residents, None, Free, Confined));

            // The same residents, mover arriving CONFINED: remainder {2 outside} + mover confined is legal.
            Assert.IsFalse(IpLimitManager.ArrivalViolates(true, residents, None, Free, Confined));
        }

        [TestMethod]
        public void NoResidents_MoverOutside_Allowed()
        {
            Assert.IsFalse(IpLimitManager.ArrivalViolates(false, new List<IpLimitCandidate>(), None, Free, Confined));
        }

        [TestMethod]
        public void MaxFreeZero_MoverOutsideAlone_Violates()
        {
            Assert.IsTrue(IpLimitManager.ArrivalViolates(false, new List<IpLimitCandidate>(), None, 0, Confined));
        }

        private static readonly IpLimitZoneDials Dials = new IpLimitZoneDials(true, true, true, true, 15);

        [TestMethod]
        public void DecideArrival_FreshActivityStamp_GivesGrace()
        {
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Grace,
                IpLimitManager.DecideArrival(true, 1000, IpLimitConfinementReason.PvpArena, 1010, Dials));
        }

        [TestMethod]
        public void DecideArrival_DepartedWithinTail_GraceEvenIfArrivalIsMuchLater()
        {
            // seenAt 1000, teleport started at 1009: fresh, regardless of how long transit and deferral took.
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Grace,
                IpLimitManager.DecideArrival(true, 1000, IpLimitConfinementReason.PvpArena, 1009, Dials));
        }

        [TestMethod]
        public void DecideArrival_DepartedAfterTail_Boots()
        {
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Boot,
                IpLimitManager.DecideArrival(true, 1000, IpLimitConfinementReason.PvpArena, 1020, Dials));
        }

        [TestMethod]
        public void DecideArrival_StaleActivityStamp_Boots()
        {
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Boot,
                IpLimitManager.DecideArrival(true, 1000, IpLimitConfinementReason.PvpArena, 1016, Dials));
        }

        [TestMethod]
        public void DecideArrival_NoStamp_Boots()
        {
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Boot,
                IpLimitManager.DecideArrival(true, null, null, 1010, Dials));
        }

        [TestMethod]
        public void DecideArrival_NotViolating_Allows()
        {
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Allow,
                IpLimitManager.DecideArrival(false, 1000, IpLimitConfinementReason.PvpArena, 1010, Dials));
        }

        [TestMethod]
        public void DecideArrival_FreshStampButDialOff_Boots()
        {
            var off = new IpLimitZoneDials(false, true, true, true, 15);
            Assert.AreEqual(IpLimitManager.ArrivalOutcome.Boot,
                IpLimitManager.DecideArrival(true, 1000, IpLimitConfinementReason.PvpArena, 1010, off));
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Player_Location.cs")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "Could not find Player_Location.cs by walking up from " + System.AppContext.BaseDirectory);
            return dir.FullName;
        }

        [TestMethod]
        public void OnTeleportComplete_DoesNotCallCheckOnArrivalInline()
        {
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Player_Location.cs")).Replace("\r\n", "\n");

            // Must be the WorldManager-qualified form: an unqualified EnqueueAction inside Player resolves to the
            // player's own landblock-thread queue and would silently undo the threading fix.
            Assert.IsTrue(text.Contains("WorldManager.EnqueueAction(new ActionEventDelegate(() => IpLimitManager.CheckOnArrival(this)))"),
                "CheckOnArrival must be marshalled onto the world queue via WorldManager.EnqueueAction");

            // Every CheckOnArrival mention must sit inside the enqueue lambda; a bare statement call is inline.
            foreach (var line in text.Split('\n'))
                if (line.Contains("IpLimitManager.CheckOnArrival(") && !line.Contains("WorldManager.EnqueueAction("))
                    Assert.Fail("inline CheckOnArrival call: " + line.Trim());
        }
    }
}