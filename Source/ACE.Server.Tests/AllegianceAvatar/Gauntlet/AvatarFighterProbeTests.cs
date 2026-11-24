using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The fighter visibility probe (AvatarFighterProbe): its lines, its gate, and the two Player_Tracking sites that call it.
    /// No PropertyManager key is read: the gate goes through the Enabled seam.
    /// </summary>
    [TestClass]
    public class AvatarFighterProbeTests
    {
        private readonly List<string> lines = new List<string>();

        [TestInitialize]
        public void Setup()
        {
            GauntletFighterFixtures.EnsureInitialized();
            AvatarFighterProbe.ResetSeams();
            AvatarFighterProbe.Sink = lines.Add;
            AvatarFighterProbe.Enabled = () => true;
        }

        [TestCleanup]
        public void Cleanup() => AvatarFighterProbe.ResetSeams();

        [TestMethod]
        public void PlacedLine_NamesEveryPlayerThePlacementCreateWentTo()
        {
            var line = AvatarFighterProbe.PlacedLine("run 7", 0x80001234, 0x00010064, 0xA9B40019, 0x40C, 2.0f, new[] { "Awfulwaffle", "Trial Anvil" });

            Assert.AreEqual("[AVATAR_FIGHTER_PROBE] placed 0x80001234 (run 7) instance 0x00010064 cell 0xA9B40019 physics 0x0000040C scale 2; placement create went to 2 player(s): Awfulwaffle, Trial Anvil", line);
        }

        [TestMethod]
        public void PlacedLine_NoKnownPlayers_SaysNone()
        {
            var line = AvatarFighterProbe.PlacedLine("run 7", 1, 2, 3, 4, null, new string[0]);

            StringAssert.EndsWith(line, "scale unset; placement create went to 0 player(s): none");
        }

        [TestMethod]
        public void ClientLine_CarriesAdminvisionInstancesDistanceAndTheDelayFlag()
        {
            var line = AvatarFighterProbe.ClientLine("create sent", "Trial Anvil", 0x50000002, false, 0x00010064, 0xA9B40019, 0x80001234, 0x00010064, 12.34, true);

            Assert.AreEqual("[AVATAR_FIGHTER_PROBE] create sent: 0x80001234 to Trial Anvil (0x50000002) adminvision off, viewer instance 0x00010064 cell 0xA9B40019, fighter instance 0x00010064, distance 12.3 m, post-teleport delay", line);
        }

        [TestMethod]
        public void OnPlaced_LogsThroughTheSink_OnlyWhenEnabled()
        {
            var fighter = GauntletFighterFixtures.Fighter();

            AvatarFighterProbe.OnPlaced(fighter, "run 1");
            Assert.AreEqual(1, lines.Count, "enabled: one line");
            StringAssert.Contains(lines[0], $"placed 0x{fighter.Guid.Full:X8} (run 1)");

            AvatarFighterProbe.Enabled = () => false;
            AvatarFighterProbe.OnPlaced(fighter, "run 1");
            Assert.AreEqual(1, lines.Count, "disabled: nothing");
        }

        [TestMethod]
        public void AThrowingGate_NeverReachesTheCaller()
        {
            AvatarFighterProbe.Enabled = () => throw new InvalidOperationException("no PropertyManager");

            AvatarFighterProbe.OnPlaced(GauntletFighterFixtures.Fighter(), "run 1");

            Assert.AreEqual(0, lines.Count);
        }

        // ---- the two call sites (source scan: a Player with a live Session is not constructible here) -------------------

        private static string TrackingSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Player_Tracking.cs");

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n");

                dir = dir.Parent;
            }

            Assert.Fail("Could not find Player_Tracking.cs by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        internal static void AssertCreateSite(string src)
        {
            var track = src.IndexOf("public void TrackObject(", StringComparison.Ordinal);
            Assert.IsTrue(track >= 0, "TrackObject not found");

            var probe = src.IndexOf("AvatarFighterProbe.OnClientCreate(", track, StringComparison.Ordinal);
            var visibilityGate = src.IndexOf("if (worldObject.Visibility && !Adminvision)", track, StringComparison.Ordinal);
            var send = src.IndexOf("Session.Network.EnqueueSend(", track, StringComparison.Ordinal);

            Assert.IsTrue(probe > track, "TrackObject must call the probe");
            Assert.IsTrue(visibilityGate > probe, "the probe must run before the Visibility gate, or a skipped create is never logged");
            Assert.IsTrue(send > visibilityGate, "control: the send follows the gate");
        }

        internal static void AssertDeleteSite(string src)
        {
            var remove = src.IndexOf("public void RemoveTrackedObject(", StringComparison.Ordinal);
            Assert.IsTrue(remove >= 0, "RemoveTrackedObject not found");

            var next = src.IndexOf("public void TrackEquippedObject(", remove, StringComparison.Ordinal);
            var probe = src.IndexOf("AvatarFighterProbe.OnClientDelete(", remove, StringComparison.Ordinal);

            Assert.IsTrue(probe > remove && probe < next, "RemoveTrackedObject must call the delete probe");
        }

        [TestMethod]
        public void TrackObject_CallsTheCreateProbe_AheadOfTheVisibilityGate() => AssertCreateSite(TrackingSource());

        [TestMethod]
        public void RemoveTrackedObject_CallsTheDeleteProbe() => AssertDeleteSite(TrackingSource());

        [TestMethod]
        public void SiteChecks_FailOnTheMutations()
        {
            var src = TrackingSource();

            var noCreate = src.Replace("AvatarFighterProbe.OnClientCreate(", "AvatarFighterProbe.Removed(");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertCreateSite(noCreate), "a removed create probe must fail");

            // the probe moved below the gate
            var gate = "            if (worldObject.Visibility && !Adminvision)\n                return;\n";
            var probeStart = src.IndexOf("            if (worldObject is ACE.Server.Avatars.Gauntlet.GauntletAvatarFighter probedFighter)", StringComparison.Ordinal);
            var probeEnd = src.IndexOf("\n\n", probeStart, StringComparison.Ordinal) + 1;
            var probeBlock = src.Substring(probeStart, probeEnd - probeStart);
            var moved = src.Remove(probeStart, probeEnd - probeStart).Replace(gate, gate + probeBlock);
            Assert.AreNotEqual(src, moved, "the mutation must change the source");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertCreateSite(moved), "a probe below the gate must fail");

            var noDelete = src.Replace("AvatarFighterProbe.OnClientDelete(", "AvatarFighterProbe.Removed(");
            Assert.ThrowsExactly<AssertFailedException>(() => AssertDeleteSite(noDelete), "a removed delete probe must fail");
        }
    }
}
