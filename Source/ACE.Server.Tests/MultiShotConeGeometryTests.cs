using System;
using System.Numerics;

using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="Creature.IsInMultiShotCone"/>, the pure 3D-cone geometry test
    /// GetMultiShotTargets applies per candidate. Pure vector arithmetic: no landblock, no physics,
    /// no WorldObject construction.
    /// </summary>
    [TestClass]
    public class MultiShotConeGeometryTests
    {
        private static readonly Vector3 Origin = Vector3.Zero;
        private static readonly Vector3 AimDirForward = new Vector3(0, 1, 0);   // +Y

        private const float MaxRange = 40.0f;

        // matches weapon.MultiShotSpreadAngle default of 20 degrees, expressed as a slope
        private static readonly float SpreadRate20Deg = (float)Math.Tan(20.0 * Math.PI / 180.0);

        [TestMethod]
        public void CandidateStraightAheadOnAimLine_IsInCone()
        {
            var candidateAimPoint = new Vector3(0, 10, 0);

            Assert.IsTrue(Creature.IsInMultiShotCone(Origin, AimDirForward, candidateAimPoint, MaxRange, SpreadRate20Deg));
        }

        [TestMethod]
        public void CandidateAheadButFarAbove_SameXY_IsExcluded()
        {
            // reported bug: a target 10 units ahead but 30 units straight up (same XY as the aim line)
            // used to pass the old 2D fan test because the old code zeroed Z before comparing.
            var candidateAimPoint = new Vector3(0, 10, 30);

            Assert.IsFalse(Creature.IsInMultiShotCone(Origin, AimDirForward, candidateAimPoint, MaxRange, SpreadRate20Deg));
        }

        [TestMethod]
        public void CandidateAheadWithSmallLateralOffset_WithinSpread_IsInCone()
        {
            // 10 units ahead, offset 2 units laterally - well within the default spread's radius
            // (MultiShotBaseWidth 1.5 + 10 * tan(20deg) ~= 1.5 + 3.64 = ~5.14)
            var candidateAimPoint = new Vector3(2, 10, 0);

            Assert.IsTrue(Creature.IsInMultiShotCone(Origin, AimDirForward, candidateAimPoint, MaxRange, SpreadRate20Deg));
        }

        [TestMethod]
        public void CandidateBehindShooter_IsExcluded()
        {
            var candidateAimPoint = new Vector3(0, -5, 0);

            Assert.IsFalse(Creature.IsInMultiShotCone(Origin, AimDirForward, candidateAimPoint, MaxRange, SpreadRate20Deg));
        }

        [TestMethod]
        public void CandidateBeyondMaxRange_IsExcluded()
        {
            var candidateAimPoint = new Vector3(0, MaxRange + 5.0f, 0);

            Assert.IsFalse(Creature.IsInMultiShotCone(Origin, AimDirForward, candidateAimPoint, MaxRange, SpreadRate20Deg));
        }

        [TestMethod]
        public void CandidateFarAbove_WithAimLinePitchedUpTowardIt_IsInCone()
        {
            // same candidate as the excluded case above, but the aim line itself is pitched steeply
            // up toward it - the cone follows the real 3D aim line, so this now passes.
            var candidateAimPoint = new Vector3(0, 10, 30);
            var aimDirUp = Vector3.Normalize(candidateAimPoint - Origin);

            Assert.IsTrue(Creature.IsInMultiShotCone(Origin, aimDirUp, candidateAimPoint, MaxRange, SpreadRate20Deg));
        }
    }
}
