using System;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the Spell AOE secondary-projectile launch vector (SpellProjectile.GetAoeChildAimOffset).
    ///
    /// The regression these exist for: the aim used to ASSIGN over the Z of the creature-to-creature offset
    /// rather than adding to it, discarding the elevation difference between the struck target and its
    /// neighbor. Every blast then travelled along the struck target's own ground plane, so on a ramp or
    /// stairs the child projectile flew over or under a neighbor the (spherical) range check had already
    /// accepted, and the AOE landed on nobody. Flat ground hid it, because there the elevation term is 0.
    ///
    /// Target selection itself (radius, hostile filters) lives in SpellAoeAbility and needs live creatures;
    /// only the pure aim math is covered here.
    /// </summary>
    [TestClass]
    public class SpellAoeAimTests
    {
        // The spawn point is the struck target's feet + 75% of its height; the aim point is the neighbor's
        // feet + 50% of its height. Both creatures are 2.0m tall in these cases unless stated otherwise.
        private const float Height = 2.0f;
        private const float BodyCorrection = (Height / 2.0f) - (Height * 0.75f);   // -0.5m

        [TestMethod]
        public void FlatGround_AimsSlightlyDown_ByTheBodyHeightDifferenceAlone()
        {
            // 5m due east, no elevation change
            var offset = SpellProjectile.GetAoeChildAimOffset(new Vector3(5.0f, 0.0f, 0.0f), Height, Height);

            Assert.AreEqual(5.0f, offset.X, 0.0001f);
            Assert.AreEqual(0.0f, offset.Y, 0.0001f);
            Assert.AreEqual(BodyCorrection, offset.Z, 0.0001f);
        }

        [TestMethod]
        public void UpSlope_KeepsTheElevationGain()
        {
            // neighbor 5m away and 3m HIGHER up a ramp
            var offset = SpellProjectile.GetAoeChildAimOffset(new Vector3(5.0f, 0.0f, 3.0f), Height, Height);

            // 3m of climb, less the 0.5m chest-to-mid-body correction - the blast must aim UP
            Assert.AreEqual(3.0f + BodyCorrection, offset.Z, 0.0001f);
            Assert.IsTrue(offset.Z > 0.0f, "an uphill neighbor must be aimed at above the horizontal");
        }

        [TestMethod]
        public void DownSlope_KeepsTheElevationDrop()
        {
            // neighbor 5m away and 3m LOWER down a ramp
            var offset = SpellProjectile.GetAoeChildAimOffset(new Vector3(5.0f, 0.0f, -3.0f), Height, Height);

            Assert.AreEqual(-3.0f + BodyCorrection, offset.Z, 0.0001f);
            Assert.IsTrue(offset.Z < 0.0f, "a downhill neighbor must be aimed at below the horizontal");
        }

        [TestMethod]
        public void ElevationChange_ActuallyChangesTheLaunchDirection()
        {
            // The bug's signature: flat and sloped produced the SAME normalized direction. They must not.
            var flat = Vector3.Normalize(SpellProjectile.GetAoeChildAimOffset(new Vector3(5.0f, 0.0f, 0.0f), Height, Height));
            var ramp = Vector3.Normalize(SpellProjectile.GetAoeChildAimOffset(new Vector3(5.0f, 0.0f, 3.0f), Height, Height));

            Assert.IsTrue(Math.Abs(ramp.Z - flat.Z) > 0.1f,
                $"launch direction ignored the elevation difference (flat Z {flat.Z}, ramp Z {ramp.Z})");
        }

        [TestMethod]
        public void HorizontalComponents_AreNeverAltered()
        {
            var offset = SpellProjectile.GetAoeChildAimOffset(new Vector3(-3.5f, 4.25f, 2.0f), 1.2f, 3.4f);

            Assert.AreEqual(-3.5f, offset.X, 0.0001f);
            Assert.AreEqual(4.25f, offset.Y, 0.0001f);
        }

        [TestMethod]
        public void MismatchedHeights_UseEachCreaturesOwnBodyPoint()
        {
            // primary 4m tall (spawn at 3.0m), neighbor 1m tall (aim at 0.5m), same ground level
            var offset = SpellProjectile.GetAoeChildAimOffset(new Vector3(6.0f, 0.0f, 0.0f), 4.0f, 1.0f);

            Assert.AreEqual(0.5f - 3.0f, offset.Z, 0.0001f);
        }

        [TestMethod]
        public void Degenerate_ReturnsZero_SoTheCallerCanDropIt()
        {
            // co-located, equal heights: the body correction cancels and there is no direction to launch in
            var offset = SpellProjectile.GetAoeChildAimOffset(Vector3.Zero, Height, Height * 1.5f);

            Assert.AreEqual(Vector3.Zero, offset);
        }

        [TestMethod]
        public void CoLocated_ButDifferentElevation_IsStillLaunchable()
        {
            // directly overhead (a balcony, a stairwell) is a real aim, not a degenerate one
            var offset = SpellProjectile.GetAoeChildAimOffset(new Vector3(0.0f, 0.0f, 4.0f), Height, Height);

            Assert.AreNotEqual(Vector3.Zero, offset);
            Assert.AreEqual(4.0f + BodyCorrection, offset.Z, 0.0001f);
        }
    }
}
