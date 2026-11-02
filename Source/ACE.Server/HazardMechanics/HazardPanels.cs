using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The shared panel helpers of the fill and fog mechanics (fan-out invariant 1): colour -> weenie, the rotations
    /// that stand a single-sided 10 x 10 m panel up, down or on its edge, the scale and translucency arithmetic, the
    /// one place a panel spawn spec is built, and the region refusal both mechanics share.
    ///
    /// ROTATIONS. System.Numerics builds a Quaternion as (x, y, z, w); the world SQL writes (w, x, y, z). Every rotation
    /// here is made from <see cref="Quaternion.CreateFromAxisAngle"/>/<see cref="Quaternion.Concatenate"/> so neither
    /// order is typed by hand. A panel faces along its local +Z, so each helper names the world direction that normal
    /// is turned to.
    /// </summary>
    public static class HazardPanels
    {
        private static readonly string[] colours = { "acid", "lava", "smoke", "poison" };

        /// <summary>The accepted colour words ("poison" is the acid panel).</summary>
        public static IReadOnlyList<string> Colours => colours;

        /// <summary>The panel weenie for a colour word (case-insensitive); false for an unknown word.</summary>
        public static bool TryWcid(string colour, out uint wcid)
        {
            switch (colour?.Trim().ToLowerInvariant())
            {
                case "acid":
                case "poison":
                    wcid = HazardDefaults.PanelAcidWcid;
                    return true;
                case "lava":
                    wcid = HazardDefaults.PanelLavaWcid;
                    return true;
                case "smoke":
                    wcid = HazardDefaults.PanelSmokeWcid;
                    return true;
                default:
                    wcid = 0;
                    return false;
            }
        }

        /// <summary>
        /// The Translucency property for an opacity: 1 - clamp(opacity, 0, 1), or null (leave it unset) when that is
        /// under 0.001 - the networking layer sends no translucency below that (Landblock.cs cloud precedent).
        /// </summary>
        public static float? TranslucencyFor(double opacity)
        {
            if (!double.IsFinite(opacity))
                return null;

            var t = 1.0 - Math.Min(1.0, Math.Max(0.0, opacity));

            return t < 0.001 ? (float?)null : (float)t;
        }

        /// <summary>
        /// How far a panel's FRONT (the way its normal points) must stand from any floor or wall for the panel to spawn,
        /// and snap, exactly where it is put: 0.2 m, the engine's whole placement sphere, plus 0.01 m margin.
        ///
        /// WHY (2026-10-27, the Matron fill abort). The panel Setup 0x02000E6E declares NO collision spheres, so the
        /// engine places it by PhysicsGlobals.DummySphere (r 0.1, centred 0.1 m up its local +Z, never scaled:
        /// PhysicsObj.SetPosition, InitSphere(1, DummySphere, 1.0)). That sphere sits on the panel's FRONT face, so
        /// turning the panel turns the sphere with it. PhysicsObj.AdjustPosition refuses the spawn when the sphere's
        /// centre is in no cell, and the placement pushes the body out until the whole sphere is clear. Measured on
        /// 0x586B (floor z -60.0) with the real dats (MatronFillSpawnTests): a face-DOWN panel at -59.9 or lower is
        /// refused, between -59.899 and -59.801 it comes to rest at -59.8, and from -59.8 up it rests exactly where it
        /// was put; a face-UP panel at -59.95 rests exactly there. The curtain fronts that face the wall from 0.05 m
        /// are refused the same way (at 0.199 m four of the five openings still do not place all eight panels exactly
        /// where put; at 0.2 m all five do).
        /// </summary>
        public const float FaceClearance = 0.21f;

        /// <summary>The spec scale that gives a panel an edge of <paramref name="edge"/> metres.</summary>
        public static float ScaleFor(float edge) => edge / HazardDefaults.PanelEdgeAtScale1;

        /// <summary>Normal +Z (a floor-style panel that faces up).</summary>
        public static Quaternion FaceUp => Quaternion.Identity;

        /// <summary>Normal -Z (a ceiling-style panel that faces down).</summary>
        public static Quaternion FaceDown => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

        /// <summary>Normal +Y (<paramref name="positive"/>) or -Y, the panel standing upright.</summary>
        public static Quaternion FaceAlongY(bool positive)
            => Quaternion.CreateFromAxisAngle(Vector3.UnitX, positive ? -MathF.PI / 2f : MathF.PI / 2f);

        /// <summary>Normal +X (<paramref name="positive"/>) or -X, the panel standing upright.</summary>
        public static Quaternion FaceAlongX(bool positive)
            => Quaternion.CreateFromAxisAngle(Vector3.UnitY, positive ? MathF.PI / 2f : -MathF.PI / 2f);

        /// <summary>The same upright panel turned to face the opposite horizontal way (a doubled fog panel's partner).</summary>
        public static Quaternion Flipped(Quaternion q)
            => Quaternion.Concatenate(q, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));

        /// <summary>
        /// The one panel spawn spec (fan-out invariant 1). Never raised out of a solid: a panel belongs exactly where
        /// the mechanic put it. <paramref name="moving"/> is true for anything that will MoveLeg.
        /// </summary>
        public static HazardSpawnSpec Spec(uint wcid, Vector3 centre, Quaternion rotation, float edge, double opacity, bool moving, string tag)
            => new HazardSpawnSpec
            {
                Wcid = wcid,
                Position = centre,
                Rotation = rotation,
                Scale = ScaleFor(edge),
                Translucency = TranslucencyFor(opacity),
                Moving = moving,
                AllowRaise = false,
                Tag = tag,
            };

        /// <summary>
        /// Null when the region can host a panel mechanic, else one line: a contained rect room on a plane ground and a
        /// single landblock (the only geometry the panel tiling is defined for in this build).
        /// </summary>
        public static string RegionRefusal(string kind, HazardRegion region)
        {
            if (region == null)
                return "no region";

            if (region.Shape != HazardShape.Rect)
                return $"{kind} needs a rect region (contained rooms only in this build)";

            if (region.GroundMode != HazardGroundMode.Plane || region.SpansLandblocks)
                return $"{kind} needs a plane ground (contained rooms only in this build)";

            return null;
        }
    }
}
