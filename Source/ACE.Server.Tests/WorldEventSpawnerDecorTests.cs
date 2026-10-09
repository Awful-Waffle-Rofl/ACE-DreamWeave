using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-14 decor path in <see cref="WorldEventSpawner"/>. Only the pure members are
    /// exercised (TECH-DESIGN D6): the placement helpers and the kind/cleanup rules. The spawn loop itself
    /// needs a live landblock and a real WorldObject and therefore goes to the verify queue, not CI - the
    /// point of splitting these statics out is that the DECISIONS are testable even though the placement
    /// is not.
    /// </summary>
    [TestClass]
    public class WorldEventSpawnerDecorTests
    {
        // Same outdoor cell shape WorldEventGeometryTests uses: cell bits below 0x100 (outdoors) and
        // non-zero, so the Position constructor does not rebase X/Y through SetPosition.
        private const uint OutdoorCell = 0x016C0025;

        private const float CentreX = 96f;
        private const float CentreY = 96f;
        private const float CentreZ = 42f;

        private const uint TestInstance = 7u;

        private static Position Centre()
        {
            return new Position(OutdoorCell, CentreX, CentreY, CentreZ, 0f, 0f, 0f, 1f, TestInstance);
        }

        // ---- rotation ----------------------------------------------------------------------------------

        [TestMethod]
        public void DecorRotation_Upright_IsIdentity()
        {
            var rotation = WorldEventSpawner.DecorRotation(inverted: false);

            Assert.AreEqual(Quaternion.Identity, rotation);
            Assert.AreEqual(1f, rotation.W, 0.0001f);
            Assert.AreEqual(0f, rotation.X, 0.0001f);
            Assert.AreEqual(0f, rotation.Y, 0.0001f);
            Assert.AreEqual(0f, rotation.Z, 0.0001f);
        }

        /// <summary>
        /// The 180 degree flip about X: w=0, x=1, y=0, z=0. This is what puts the visible disc BELOW the
        /// object origin instead of above it, which is the whole reason a high stack can keep its origins
        /// low enough to stay above terrain and inside the client's animation range.
        /// </summary>
        [TestMethod]
        public void DecorRotation_Inverted_IsAHalfTurnAboutX()
        {
            var rotation = WorldEventSpawner.DecorRotation(inverted: true);

            Assert.AreEqual(0f, rotation.W, 0.0001f);
            Assert.AreEqual(1f, rotation.X, 0.0001f);
            Assert.AreEqual(0f, rotation.Y, 0.0001f);
            Assert.AreEqual(0f, rotation.Z, 0.0001f);

            Assert.AreNotEqual(Quaternion.Identity, rotation);
        }

        // ---- rotation: WP-15 yaw x inverted composition ------------------------------------------------

        /// <summary>
        /// The WP-14 overload must keep meaning exactly what it meant, or the whole Sky Rift stack moves.
        /// </summary>
        [TestMethod]
        public void DecorRotation_LegacyOverload_IsYawZero()
        {
            Assert.AreEqual(WorldEventSpawner.DecorRotation(inverted: false, yawDegrees: 0f),
                WorldEventSpawner.DecorRotation(inverted: false));

            Assert.AreEqual(WorldEventSpawner.DecorRotation(inverted: true, yawDegrees: 0f),
                WorldEventSpawner.DecorRotation(inverted: true));
        }

        [TestMethod]
        public void DecorRotation_Yaw0Upright_IsIdentity()
        {
            var rotation = WorldEventSpawner.DecorRotation(inverted: false, yawDegrees: 0f);

            Assert.AreEqual(1f, rotation.W, 0.00001f);
            Assert.AreEqual(0f, rotation.X, 0.00001f);
            Assert.AreEqual(0f, rotation.Y, 0.00001f);
            Assert.AreEqual(0f, rotation.Z, 0.00001f);
        }

        [TestMethod]
        public void DecorRotation_Yaw0Inverted_IsTheHalfTurnAboutX()
        {
            var rotation = WorldEventSpawner.DecorRotation(inverted: true, yawDegrees: 0f);

            Assert.AreEqual(0f, rotation.W, 0.00001f);
            Assert.AreEqual(1f, rotation.X, 0.00001f);
            Assert.AreEqual(0f, rotation.Y, 0.00001f);
            Assert.AreEqual(0f, rotation.Z, 0.00001f);
        }

        /// <summary>A quarter turn about +Z: (x=0, y=0, z=sin 45, w=cos 45).</summary>
        [TestMethod]
        public void DecorRotation_Yaw90Upright_IsAQuarterTurnAboutZ()
        {
            var rotation = WorldEventSpawner.DecorRotation(inverted: false, yawDegrees: 90f);

            Assert.AreEqual(0.70710678f, rotation.W, 0.00001f);
            Assert.AreEqual(0f, rotation.X, 0.00001f);
            Assert.AreEqual(0f, rotation.Y, 0.00001f);
            Assert.AreEqual(0.70710678f, rotation.Z, 0.00001f);
        }

        /// <summary>
        /// The ORDER of the composition, pinned by construction rather than by prose: yaw first, then the
        /// flip about the WORLD X axis, which in System.Numerics is Multiply(flip, yaw). The two orders give
        /// different quaternions, so this test would fail if production swapped them - which is the point.
        /// </summary>
        [TestMethod]
        public void DecorRotation_Yaw90Inverted_IsFlipTimesYaw()
        {
            var flip = new Quaternion(1f, 0f, 0f, 0f);
            var yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));

            var expected = Quaternion.Multiply(flip, yaw90);

            var rotation = WorldEventSpawner.DecorRotation(inverted: true, yawDegrees: 90f);

            Assert.AreEqual(expected.W, rotation.W, 0.00001f);
            Assert.AreEqual(expected.X, rotation.X, 0.00001f);
            Assert.AreEqual(expected.Y, rotation.Y, 0.00001f);
            Assert.AreEqual(expected.Z, rotation.Z, 0.00001f);

            var wrongOrder = Quaternion.Multiply(yaw90, flip);

            Assert.IsFalse(Math.Abs(wrongOrder.Y - rotation.Y) < 0.00001f
                           && Math.Abs(wrongOrder.Z - rotation.Z) < 0.00001f,
                "the two operand orders must be distinguishable, or this test pins nothing");
        }

        /// <summary>
        /// The Weave Spiral's four arms: one wcid, four yaws a quarter turn apart, all upright. Every arm
        /// must come out with a distinct rotation or the scene reads as a single rig.
        /// </summary>
        [TestMethod]
        public void DecorRotation_WeaveSpiralArms_AreFourDistinctQuarterTurns()
        {
            var rotations = new[]
            {
                WorldEventSpawner.DecorRotation(false, 0f),
                WorldEventSpawner.DecorRotation(false, 90f),
                WorldEventSpawner.DecorRotation(false, 180f),
                WorldEventSpawner.DecorRotation(false, 270f)
            };

            for (var i = 0; i < rotations.Length; i++)
            {
                for (var j = i + 1; j < rotations.Length; j++)
                    Assert.AreNotEqual(rotations[i], rotations[j], $"arms {i} and {j} must not share a rotation");
            }

            // A yaw is a pure turn about the vertical, so X and Y stay 0 whatever the angle.
            foreach (var rotation in rotations)
            {
                Assert.AreEqual(0f, rotation.X, 0.00001f);
                Assert.AreEqual(0f, rotation.Y, 0.00001f);
            }
        }

        [TestMethod]
        public void DecorPosition_CarriesTheYawRotation()
        {
            var position = WorldEventSpawner.DecorPosition(Centre(), 0f, inverted: false, yawDegrees: 270f);

            Assert.AreEqual(WorldEventSpawner.DecorRotation(false, 270f), position.Rotation);
            Assert.AreEqual(CentreZ, position.PositionZ, 0.0001f, "a weave_spiral arm sits at the anchor's Z");
        }

        // ---- position ----------------------------------------------------------------------------------

        [TestMethod]
        public void DecorPosition_RaisesZ_ByExactlyDz()
        {
            var position = WorldEventSpawner.DecorPosition(Centre(), 67.7f, inverted: true);

            Assert.AreEqual(CentreZ + 67.7f, position.PositionZ, 0.0001f);
        }

        [TestMethod]
        public void DecorPosition_LeavesXYCellAndInstanceAlone()
        {
            var centre = Centre();

            var position = WorldEventSpawner.DecorPosition(centre, 32.4f, inverted: true);

            Assert.AreEqual(CentreX, position.PositionX, 0.0001f, "decor sits directly over the geometry centre");
            Assert.AreEqual(CentreY, position.PositionY, 0.0001f, "decor sits directly over the geometry centre");
            Assert.AreEqual(centre.Cell, position.Cell, "raising Z must not rebase the landblock or land cell");
            Assert.AreEqual(centre.Instance, position.Instance, "the instance must follow the anchor");
            Assert.AreEqual(centre.InstancedLandblock, position.InstancedLandblock);
        }

        [TestMethod]
        public void DecorPosition_DoesNotMutateTheCentre()
        {
            var centre = Centre();

            WorldEventSpawner.DecorPosition(centre, 58.3f, inverted: true);

            Assert.AreEqual(CentreZ, centre.PositionZ, 0.0001f, "the shared anchor position must be copied, not moved");
            Assert.AreEqual(Quaternion.Identity, centre.Rotation, "the shared anchor rotation must not be flipped");
        }

        [TestMethod]
        public void DecorPosition_CarriesTheRotation()
        {
            var inverted = WorldEventSpawner.DecorPosition(Centre(), 10f, inverted: true);
            var upright = WorldEventSpawner.DecorPosition(Centre(), 10f, inverted: false);

            Assert.AreEqual(WorldEventSpawner.DecorRotation(true), inverted.Rotation);
            Assert.AreEqual(WorldEventSpawner.DecorRotation(false), upright.Rotation);
        }

        /// <summary>
        /// The whole Sky Rift stack, from the same numbers sources.json ships: five origins over one centre,
        /// each 0.3 m of DISC height apart once 0.455 x scale is subtracted back off.
        /// </summary>
        [TestMethod]
        public void DecorPosition_SkyRiftStack_DiscsAre300mmApart()
        {
            var centre = Centre();

            var layers = new[]
            {
                (scale: 100f, dz: 67.7f),
                (scale: 80f, dz: 58.3f),
                (scale: 60f, dz: 48.9f),
                (scale: 40f, dz: 39.5f),
                (scale: 25f, dz: 32.4f)
            };

            var discHeights = new double[layers.Length];

            for (var i = 0; i < layers.Length; i++)
            {
                var origin = WorldEventSpawner.DecorPosition(centre, layers[i].dz, inverted: true);

                // Inverted, so the disc renders 0.455 x scale BELOW the origin.
                discHeights[i] = origin.PositionZ - 0.455 * layers[i].scale - CentreZ;

                Assert.IsTrue(origin.PositionZ > centre.PositionZ, $"layer {i} origin must be above the anchor");
            }

            for (var i = 1; i < discHeights.Length; i++)
                Assert.AreEqual(0.3, discHeights[i - 1] - discHeights[i], 0.03, $"disc gap {i - 1} to {i}");
        }

        [TestMethod]
        public void DecorPosition_NullCentre_ReturnsNull()
        {
            Assert.IsNull(WorldEventSpawner.DecorPosition(null, 10f, inverted: true));
        }

        // ---- WP-21 pitch --------------------------------------------------------------------------------

        [TestMethod]
        public void DecorRotation_Pitch0_IsIdentity()
        {
            var rotation = WorldEventSpawner.DecorRotation(false, 0f, 0f);

            Assert.AreEqual(Quaternion.Identity, rotation);
        }

        /// <summary>
        /// Pitch 0 must reproduce every existing (inverted, yaw) result byte for byte, or every pre-WP-21
        /// theme's decor moves.
        /// </summary>
        [TestMethod]
        public void DecorRotation_Pitch0_MatchesTheTwoArgumentOverload()
        {
            foreach (var inverted in new[] { false, true })
            {
                foreach (var yaw in new[] { 0f, 90f, 180f, 270f, -720f })
                {
                    var expected = WorldEventSpawner.DecorRotation(inverted, yaw);
                    var actual = WorldEventSpawner.DecorRotation(inverted, yaw, 0f);

                    Assert.AreEqual(expected.W, actual.W, 0.00001f, $"inverted={inverted} yaw={yaw} W");
                    Assert.AreEqual(expected.X, actual.X, 0.00001f, $"inverted={inverted} yaw={yaw} X");
                    Assert.AreEqual(expected.Y, actual.Y, 0.00001f, $"inverted={inverted} yaw={yaw} Y");
                    Assert.AreEqual(expected.Z, actual.Z, 0.00001f, $"inverted={inverted} yaw={yaw} Z");
                }
            }
        }

        /// <summary>
        /// A 90 degree pitch about the object's LOCAL X axis stands a flat XY disc upright. The ACTUAL sign
        /// verified here: applying the resulting rotation to local +Z (the disc's spin axis, i.e. its face
        /// normal) lands on world -Y (SOUTH, since yaw 0 faces +Y/north per NpcRotation's remarks) rather
        /// than +Y. A content author pitching a disc at yaw 0 should read this as "the disc's face ends up
        /// pointing south" - and can add yaw 180 to flip it to face north instead.
        /// </summary>
        [TestMethod]
        public void DecorRotation_Pitch90AtYaw0_MapsLocalZToWorldMinusY()
        {
            var rotation = WorldEventSpawner.DecorRotation(false, 0f, 90f);

            var faceNormal = Vector3.Transform(Vector3.UnitZ, rotation);

            Assert.AreEqual(0f, faceNormal.X, 0.0001f);
            Assert.AreEqual(-1f, faceNormal.Y, 0.0001f, "local +Z must map to world -Y at pitch 90, yaw 0");
            Assert.AreEqual(0f, faceNormal.Z, 0.0001f);
        }

        /// <summary>
        /// Composition order (D6 - pure, pinned by construction): pitch is applied FIRST in the local frame,
        /// then yaw about world Z - Multiply(yaw, pitch) in System.Numerics' right-to-left convention.
        /// </summary>
        [TestMethod]
        public void DecorRotation_Yaw90Pitch90_IsYawTimesPitch()
        {
            var yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
            var pitch90 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(Math.PI / 2));

            var expected = Quaternion.Multiply(yaw90, pitch90);

            var rotation = WorldEventSpawner.DecorRotation(false, 90f, 90f);

            Assert.AreEqual(expected.W, rotation.W, 0.00001f);
            Assert.AreEqual(expected.X, rotation.X, 0.00001f);
            Assert.AreEqual(expected.Y, rotation.Y, 0.00001f);
            Assert.AreEqual(expected.Z, rotation.Z, 0.00001f);
        }

        [TestMethod]
        public void DecorRotation_InvertedYaw90Pitch90_IsFlipTimesYawTimesPitch()
        {
            var flip = new Quaternion(1f, 0f, 0f, 0f);
            var yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
            var pitch90 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(Math.PI / 2));

            var expected = Quaternion.Multiply(flip, Quaternion.Multiply(yaw90, pitch90));

            var rotation = WorldEventSpawner.DecorRotation(true, 90f, 90f);

            Assert.AreEqual(expected.W, rotation.W, 0.00001f);
            Assert.AreEqual(expected.X, rotation.X, 0.00001f);
            Assert.AreEqual(expected.Y, rotation.Y, 0.00001f);
            Assert.AreEqual(expected.Z, rotation.Z, 0.00001f);
        }

        // ---- WP-21 dx/dy/pitch position -------------------------------------------------------------------

        [TestMethod]
        public void DecorPosition_FullOverload_OffsetsXYAndLeavesZAtCentreDz()
        {
            var position = WorldEventSpawner.DecorPosition(Centre(), 3.5f, -1.5f, 10f, inverted: false,
                yawDegrees: 0f, pitchDegrees: 0f);

            Assert.AreEqual(CentreX + 3.5f, position.PositionX, 0.0001f);
            Assert.AreEqual(CentreY - 1.5f, position.PositionY, 0.0001f);
            Assert.AreEqual(CentreZ + 10f, position.PositionZ, 0.0001f);
        }

        [TestMethod]
        public void DecorPosition_FullOverload_ZeroOffset_MatchesTheDzOnlyOverload()
        {
            var legacy = WorldEventSpawner.DecorPosition(Centre(), 10f, inverted: true, yawDegrees: 45f);
            var full = WorldEventSpawner.DecorPosition(Centre(), 0f, 0f, 10f, inverted: true, yawDegrees: 45f, pitchDegrees: 0f);

            Assert.AreEqual(legacy.PositionX, full.PositionX, 0.0001f);
            Assert.AreEqual(legacy.PositionY, full.PositionY, 0.0001f);
            Assert.AreEqual(legacy.PositionZ, full.PositionZ, 0.0001f);
            Assert.AreEqual(legacy.Rotation, full.Rotation);
        }

        [TestMethod]
        public void DecorPosition_FullOverload_CarriesThePitchedRotation()
        {
            var position = WorldEventSpawner.DecorPosition(Centre(), 0f, 0f, 0f, inverted: false,
                yawDegrees: 0f, pitchDegrees: 90f);

            Assert.AreEqual(WorldEventSpawner.DecorRotation(false, 0f, 90f), position.Rotation);
        }

        [TestMethod]
        public void DecorPosition_FullOverload_NullCentre_ReturnsNull()
        {
            Assert.IsNull(WorldEventSpawner.DecorPosition(null, 1f, 1f, 1f, inverted: false, yawDegrees: 0f, pitchDegrees: 0f));
        }

        // ---- WP-21 fixed-offset objective post-snap dz ---------------------------------------------------

        /// <summary>
        /// The WP-15 npc dz rule (PlaceNpcs re-applies def.Dz on top of the snapped ground) applied to the
        /// Source path (see ObjectiveDef.Dz and SpawnObjectives's remarks): TryPlace re-applies this on top
        /// of whatever AdjustMapCoords/SkyDropZ already wrote, so a fixed-offset objective's dz is never
        /// silently dropped by the outdoor terrain snap.
        /// </summary>
        [TestMethod]
        public void ObjectiveSnapZ_AddsDzOnTopOfTheSnappedGround()
        {
            Assert.AreEqual(52.5f, WorldEventSpawner.ObjectiveSnapZ(42.5f, 10f), 0.0001f);
        }

        [TestMethod]
        public void ObjectiveSnapZ_ZeroDz_LeavesTheGroundZUnchanged()
        {
            Assert.AreEqual(42.5f, WorldEventSpawner.ObjectiveSnapZ(42.5f, 0f), 0.0001f);
        }

        /// <summary>
        /// Unlike SkyDropZ, a negative dz is honoured, not clamped - a fixed-offset objective may legitimately
        /// sit partway into the ground (e.g. a pillar base), and ObjectiveDef.Dz is already validated finite
        /// at load (WorldEventAxisStore.ValidateObjectives), so no further gating belongs here.
        /// </summary>
        [TestMethod]
        public void ObjectiveSnapZ_NegativeDz_LowersTheGroundZ()
        {
            Assert.AreEqual(40f, WorldEventSpawner.ObjectiveSnapZ(42.5f, -2.5f), 0.0001f);
        }

        // ---- WP-23 DecorDef.Snap -------------------------------------------------------------------------

        /// <summary>Default byte-for-byte: a theme that omits "snap" gets today's unsnapped behaviour.</summary>
        [TestMethod]
        public void DecorDef_Snap_DefaultsFalse()
        {
            var def = new DecorDef();

            Assert.IsFalse(def.Snap);
        }

        /// <summary>
        /// The formula PlaceDecor applies on top of AdjustMapCoords for a snap-enabled entry: groundZ + Dz,
        /// exactly ObjectiveSnapZ's rule (already reused by PlaceNpcs' fixed-offset objective path) - see
        /// DecorDef.Snap's remarks. Not a call into PlaceDecor itself (that needs a live landblock), but the
        /// same arithmetic, pinned so the two paths cannot silently diverge.
        /// </summary>
        [TestMethod]
        public void DecorDef_Snap_PostSnapZUsesTheSameFormulaAsObjectiveSnapZ()
        {
            const float groundZ = 42.5f;
            const float dz = 1.75f;

            Assert.AreEqual(groundZ + dz, WorldEventSpawner.ObjectiveSnapZ(groundZ, dz), 0.0001f);
        }

        // ---- WP-25 decor color styles --------------------------------------------------------------------

        /// <summary>Sky Rift-shaped decor: A,B,A,B,A by distinct-wcid first-appearance order (A=1002643, B=1002642).</summary>
        private static List<DecorDef> SkyRiftShapedDecor()
        {
            return new List<DecorDef>
            {
                new DecorDef { Wcid = 1002643, Dx = 1f, Dy = 2f, Dz = 67.7f, Scale = 100f, Speed = 0.8f, Inverted = true, Yaw = 10f, Pitch = 20f, Snap = false },
                new DecorDef { Wcid = 1002642, Dx = 3f, Dy = 4f, Dz = 58.3f, Scale = 80f,  Speed = 0.941f, Inverted = true, Yaw = 30f, Pitch = 40f, Snap = false },
                new DecorDef { Wcid = 1002643, Dx = 5f, Dy = 6f, Dz = 48.9f, Scale = 60f,  Speed = 1.143f, Inverted = true, Yaw = 50f, Pitch = 60f, Snap = false },
                new DecorDef { Wcid = 1002642, Dx = 7f, Dy = 8f, Dz = 39.5f, Scale = 40f,  Speed = 1.455f, Inverted = true, Yaw = 70f, Pitch = 80f, Snap = false },
                new DecorDef { Wcid = 1002643, Dx = 9f, Dy = 10f, Dz = 32.4f, Scale = 25f, Speed = 2.0f, Inverted = true, Yaw = 90f, Pitch = 100f, Snap = false }
            };
        }

        [TestMethod]
        public void ApplyDecorStyle_SubstitutesByDistinctFirstAppearance()
        {
            var decor = SkyRiftShapedDecor();
            var style = new List<uint> { 1002645u, 1002644u }; // C, D

            var styled = WorldEventSpawner.ApplyDecorStyle(decor, style);

            Assert.AreEqual(5, styled.Count);

            var expectedWcids = new uint[] { 1002645, 1002644, 1002645, 1002644, 1002645 };

            for (var i = 0; i < styled.Count; i++)
                Assert.AreEqual(expectedWcids[i], styled[i].Wcid, $"layer {i} wcid");

            // Every non-wcid field must be preserved verbatim.
            for (var i = 0; i < styled.Count; i++)
            {
                Assert.AreEqual(decor[i].Dx, styled[i].Dx, 0.0001f, $"layer {i} dx");
                Assert.AreEqual(decor[i].Dy, styled[i].Dy, 0.0001f, $"layer {i} dy");
                Assert.AreEqual(decor[i].Dz, styled[i].Dz, 0.0001f, $"layer {i} dz");
                Assert.AreEqual(decor[i].Scale, styled[i].Scale, 0.0001f, $"layer {i} scale");
                Assert.AreEqual(decor[i].Speed, styled[i].Speed, 0.0001f, $"layer {i} speed");
                Assert.AreEqual(decor[i].Inverted, styled[i].Inverted, $"layer {i} inverted");
                Assert.AreEqual(decor[i].Yaw, styled[i].Yaw, 0.0001f, $"layer {i} yaw");
                Assert.AreEqual(decor[i].Pitch, styled[i].Pitch, 0.0001f, $"layer {i} pitch");
                Assert.AreEqual(decor[i].Snap, styled[i].Snap, $"layer {i} snap");
            }
        }

        [TestMethod]
        public void ApplyDecorStyle_DoesNotMutateTheOriginalDefs()
        {
            var decor = SkyRiftShapedDecor();
            var style = new List<uint> { 1002645u, 1002644u };

            WorldEventSpawner.ApplyDecorStyle(decor, style);

            var expectedOriginalWcids = new uint[] { 1002643, 1002642, 1002643, 1002642, 1002643 };

            for (var i = 0; i < decor.Count; i++)
                Assert.AreEqual(expectedOriginalWcids[i], decor[i].Wcid, $"original layer {i} must still hold its authored wcid");
        }

        [TestMethod]
        public void ApplyDecorStyle_ReturnsNewInstances_NotTheSameReferences()
        {
            var decor = SkyRiftShapedDecor();
            var style = new List<uint> { 1002645u, 1002644u };

            var styled = WorldEventSpawner.ApplyDecorStyle(decor, style);

            for (var i = 0; i < decor.Count; i++)
                Assert.AreNotSame(decor[i], styled[i], $"layer {i} must be a new instance, theme defs are shared store state");
        }

        [TestMethod]
        public void ApplyDecorStyle_NullOrEmptyStyle_ReturnsInputUnchanged()
        {
            var decor = SkyRiftShapedDecor();

            Assert.AreSame(decor, WorldEventSpawner.ApplyDecorStyle(decor, null));
            Assert.AreSame(decor, WorldEventSpawner.ApplyDecorStyle(decor, new List<uint>()));
        }

        [TestMethod]
        public void ApplyDecorStyle_WrongCount_ReturnsInputUnchanged()
        {
            var decor = SkyRiftShapedDecor();

            // Sky-rift-shaped decor has 2 distinct wcids; a 3-entry style does not match.
            Assert.AreSame(decor, WorldEventSpawner.ApplyDecorStyle(decor, new List<uint> { 1u, 2u, 3u }));
        }

        [TestMethod]
        public void PickDecorStyle_SeededRandom_IsDeterministic()
        {
            var styles = new List<List<uint>>
            {
                new List<uint> { 1002643, 1002642 },
                new List<uint> { 1002645, 1002644 },
                new List<uint> { 1002647, 1002646 },
                new List<uint> { 1002649, 1002642 }
            };

            var pickedA = WorldEventSpawner.PickDecorStyle(styles, new Random(12345));
            var pickedB = WorldEventSpawner.PickDecorStyle(styles, new Random(12345));

            CollectionAssert.AreEqual(pickedA.ToList(), pickedB.ToList(), "the same seed must produce the same pick");
            Assert.IsTrue(styles.Any(s => s.SequenceEqual(pickedA)), "the pick must be one of the styles offered");
        }

        [TestMethod]
        public void PickDecorStyle_NullOrEmptyStyles_ReturnsNull()
        {
            Assert.IsNull(WorldEventSpawner.PickDecorStyle(null, new Random(1)));
            Assert.IsNull(WorldEventSpawner.PickDecorStyle(new List<List<uint>>(), new Random(1)));
        }

        [TestMethod]
        public void PickDecorStyle_NullRng_FallsBackToProductionDraw_DoesNotThrow()
        {
            var styles = new List<List<uint>> { new List<uint> { 1002643, 1002642 } };

            var picked = WorldEventSpawner.PickDecorStyle(styles, null);

            Assert.IsNotNull(picked);
            CollectionAssert.AreEqual(new uint[] { 1002643, 1002642 }, picked.ToList());
        }

        // ---- kind rules --------------------------------------------------------------------------------

        /// <summary>
        /// Decor is the ONLY kind that may be something other than a Creature - the "it can never die, so it
        /// would hold the objective open" refusal must not reject it.
        /// </summary>
        [TestMethod]
        public void RequiresCreature_IsTrueForEveryKindExceptDecor()
        {
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Wave));
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Source));
            Assert.IsTrue(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Boss));
            Assert.IsFalse(WorldEventSpawner.RequiresCreature(WorldEventSpawnKind.Decor));
        }

        /// <summary>MaxAlive budgets trash pressure only - and decor is not pressure of any kind.</summary>
        [TestMethod]
        public void CountsAsWavePressure_IsWaveOnly()
        {
            Assert.IsTrue(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Wave));
            Assert.IsFalse(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Source));
            Assert.IsFalse(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Boss));
            Assert.IsFalse(WorldEventSpawner.CountsAsWavePressure(WorldEventSpawnKind.Decor));
        }

        /// <summary>LiveTotal is "everything still standing that can be fought". Decor is reported separately.</summary>
        [TestMethod]
        public void CountsAsLive_ExcludesDecorOnly()
        {
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Wave));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Source));
            Assert.IsTrue(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Boss));
            Assert.IsFalse(WorldEventSpawner.CountsAsLive(WorldEventSpawnKind.Decor));
        }

        [TestMethod]
        public void FreshSpawner_ReportsNoDecor()
        {
            var spawner = new WorldEventSpawner();

            Assert.AreEqual(0, spawner.DecorCount);
            Assert.AreEqual(0, spawner.LiveCount);
            Assert.AreEqual(0, spawner.LiveTotal);
            Assert.IsFalse(spawner.IsDecorGuid(0x80000001));
        }

        // ---- cleanup rules -----------------------------------------------------------------------------

        /// <summary>
        /// Decor carries no PropertyBool.WorldEventCache (PlaceDecor sets no bools at all), so IsCache is
        /// false for it and BOTH DestroyAll passes take it: the creature pass at Finish is what clears the
        /// sky, and the final sweep is the safety net.
        /// </summary>
        [TestMethod]
        public void DestroyedByPass_TakesDecorOnBothPasses()
        {
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: false, includeCaches: false),
                "the creature pass at Finish must destroy decor");
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: false, includeCaches: true),
                "the final sweep must destroy decor too");
        }

        [TestMethod]
        public void DestroyedByPass_ExemptsCachesFromTheCreaturePassOnly()
        {
            Assert.IsFalse(WorldEventSpawner.DestroyedByPass(isCache: true, includeCaches: false),
                "a Weave Cache must survive the creature pass so the claim window works");
            Assert.IsTrue(WorldEventSpawner.DestroyedByPass(isCache: true, includeCaches: true),
                "the final sweep closes the caches out");
        }

        [TestMethod]
        public void IsCache_IsFalseForNothing()
        {
            Assert.IsFalse(WorldEventSpawner.IsCache(null));
        }

        // ---- objective interaction ---------------------------------------------------------------------

        /// <summary>
        /// Documents CURRENT behaviour, and is deliberately not an assertion that it is desirable:
        /// KillCountObjective.OnDeathCore excludes only the theme's objective (rift) guids, so a raw decor
        /// guid handed to it WOULD be counted as a kill.
        ///
        /// What actually prevents that is compile-time, not this exclusion list. Decor is a Generic
        /// WorldObject, never a Creature: it is adopted without P_WorldEvent, and the only route into an
        /// objective is Creature.Die -> WorldEventManager.OnEventCreatureDied(Creature, ...) ->
        /// WorldEvent.OnCreatureDied -> IWorldEventObjective.OnCreatureDied, every link of which is typed to
        /// Creature. A non-Creature cannot reach OnDeathCore at all.
        ///
        /// If a non-Creature death path is ever added, WorldEventSpawner.IsDecorGuid(uint) is the hook: the
        /// objective's exclusion func (WorldEventObjectiveFactory, which WP-14 does not own) would combine it
        /// with SourceGuids. This test is the tripwire that will start failing the day that is done.
        /// </summary>
        [TestMethod]
        public void KillCountObjective_DoesNotExcludeADecorGuid_GuardIsTheCreatureTypedChain()
        {
            var goal = new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = MvpRule.MostKills,
                Count = new ScaledCount { Base = 5, PerParticipant = 0, Cap = 5 },
                ProgressTemplate = "{killed} of {target} slain."
            };

            var objective = new KillCountObjective(goal, () => 0, () => Array.Empty<uint>(),
                new WorldEventParticipation(() => 0d));

            Assert.AreEqual("0 of 5 slain.", objective.ProgressText);

            // A guid of the shape decor would carry, reported as a stamped event death.
            objective.OnDeathCore(0x8000BEEFu, 1002643u, wasEventCreature: true, "Someone", "Someone");

            Assert.AreEqual("1 of 5 slain.", objective.ProgressText,
                "as built, a decor guid IS counted - the guard against it is the Creature-typed death chain, not this list");

            // The counterpart that IS excluded, for contrast: an objective (rift) guid.
            var withSource = new KillCountObjective(goal, () => 0, () => new[] { 0x8000BEEFu },
                new WorldEventParticipation(() => 0d));

            withSource.OnDeathCore(0x8000BEEFu, 1002603u, wasEventCreature: true, "Someone", "Someone");

            Assert.AreEqual("0 of 5 slain.", withSource.ProgressText, "objective spawns are excluded by guid");
        }
    }
}
