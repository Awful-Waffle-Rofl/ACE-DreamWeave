using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.PuzzleGates;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// A beam script's mesh hangs below its host, so a floor host of it is turned (WispSquadMechanic's mark_dest). Every
    /// beam the fork stands on the floor must be in the set, and the fire pillar (which rises on its own) must not.
    /// </summary>
    [TestClass]
    public class HangingBeamScriptTests
    {
        [TestMethod]
        public void TheWispMarkAndEveryMemoryBeamHang()
        {
            Assert.IsTrue(HazardDefaults.HangsBelowHost(HazardDefaults.WispDestMarkScript));
            Assert.IsTrue(HazardDefaults.HangsBelowHost(HazardDefaults.BeamScript));
            Assert.IsTrue(HazardDefaults.HangsBelowHost(HazardDefaults.BeamAltScript));
            Assert.IsTrue(HazardDefaults.HangsBelowHost(HazardDefaults.MemoryBeamBlueScript));
            Assert.IsTrue(HazardDefaults.HangsBelowHost(HazardDefaults.MemoryBeamGreenScript));
            Assert.IsTrue(HazardDefaults.HangsBelowHost(HazardDefaults.MemoryBeamPurpleScript));
        }

        [TestMethod]
        public void EveryWorldEventMarkerHangs()
        {
            foreach (var e in System.Enum.GetValues<WorldEventHazardElement>())
            {
                var marker = WorldEventHazardElements.For(e).MarkerScript;
                Assert.IsTrue(HazardDefaults.HangsBelowHost(marker), $"{e} marker 0x{marker:X8} is not a known hanging beam");
            }
        }

        [TestMethod]
        public void EveryPuzzleColourHangs()
        {
            foreach (var colour in PuzzleGateColours.Palette)
                Assert.IsTrue(HazardDefaults.HangsBelowHost(colour.Script), $"{colour.Name} 0x{colour.Script:X8} is not a known hanging beam");
        }

        [TestMethod]
        public void ThePillarDoesNotHang()
            => Assert.IsFalse(HazardDefaults.HangsBelowHost(HazardDefaults.PillarScript));

        [TestMethod]
        public void AFloorBeamIsTurnedToRiseAndAnyOtherEffectIsNot()
        {
            // turned: local -Z (where the mesh hangs) maps to world +Z
            var beam = Vector3.Transform(-Vector3.UnitZ, MemoryLayout.FloorOrientation(HazardDefaults.WispDestMarkScript));
            Assert.IsTrue(beam.Z > 0.99f, $"beam mark hangs along {beam}");

            Assert.AreEqual(Quaternion.Identity, MemoryLayout.FloorOrientation(HazardDefaults.PillarScript));
        }
    }
}
