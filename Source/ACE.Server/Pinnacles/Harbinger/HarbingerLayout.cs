using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.Pinnacles.Matron;
using ACE.Server.PuzzleGates;

namespace ACE.Server.Pinnacles.Harbinger
{
    /// <summary>
    /// Pure geometry of the Harbinger's Lair (landblock 0x00BC, Docs/Knots/HARBINGER-ENCOUNTER-DESIGN.md section 3), from
    /// each alcove's outward unit vector alone: no branch on which alcove it is (fan-out invariant). Every function
    /// returns landblock-frame x, y; the cells are measured and pinned in <see cref="HarbingerData"/>, and
    /// HarbingerRoomGeometryTests proves every point against the dat. The yaw convention is the hazard spawn path's
    /// (degrees counter-clockwise from north, <see cref="PuzzleGateGenerator.YawQuaternion"/>).
    /// </summary>
    public static class HarbingerLayout
    {
        /// <summary>The chamber's centre (x, y), where the Harbinger stands.</summary>
        public static readonly Vector2 ChamberCentre = new Vector2(20f, -20f);

        /// <summary>The chamber floor z, and the alcoves' floor z (the sill).</summary>
        public const float FloorZ = -36.0f;

        public const float AlcoveZ = 0.0f;

        /// <summary>The Harbinger's origin z (retail landblock_instance guid 1879818242: -35.985).</summary>
        public const float HeadZ = -35.985f;

        /// <summary>An alcove's centre stands this far from the chamber centre along its outward vector: (20, -20) + 20 o.</summary>
        public const float AlcoveDistance = 20.0f;

        /// <summary>The owner's stand point is this far back from the alcove centre (towards the doorway): c - 2.0 o.</summary>
        public const float StandBack = 2.0f;

        /// <summary>The crystal stands this far out past the alcove centre: c + 2.5 o.</summary>
        public const float CrystalOut = 2.5f;

        /// <summary>The two guardians stand this far to either side of the alcove centre: c +/- 2.5 p.</summary>
        public const float GuardSide = 2.5f;

        /// <summary>
        /// The floor portal stands this far from the chamber centre along the outward vector: (20, -20) + 8 o. Round 4 (owner
        /// playtest): it moved in from 13 m, where it stood 2 m in front of the alcove's wall stream, to 8 m, between the
        /// Harbinger's body edge (HeadRadius 4.2) and the summon ring (<see cref="SummonRingRadius"/> 10 m). It faces the chamber
        /// centre (<see cref="YawDegFacing"/> of -o, the same rule as the stand), so a player at the centre sees it face-on.
        /// </summary>
        public const float PortalOut = 8.0f;

        /// <summary>The summon ring: radius round the chamber centre at the floor z, one point per 45 degrees from due east.</summary>
        public const float SummonRingRadius = 10.0f;

        public const int SummonRingCount = 8;

        /// <summary>The Harbinger's setup (retail 36178 and 1008250).</summary>
        public const uint HeadSetup = 0x02001731;

        /// <summary>
        /// The scale the Knot copy fights him at (1008250 DefaultScale). 6.2 is the MEASURED ceiling: through the ordinary placement
        /// (WorldObject.AddPhysicsObj) the body spawns at the head up to 6.2 and is refused from 6.4 (collision spheres 0.48 x scale),
        /// and no exact-placement opt-in is used (owner ruling). HarbingerRoomGeometryTests holds the control.
        /// </summary>
        public const float HeadScale = 6.2f;

        /// <summary>
        /// MEASURED 2026-11-15: ACE.Server.Physics PhysicsObj.GetRadius() of the Harbinger body (setup 0x02001731 at
        /// <see cref="HeadScale"/>, spawned at HarbingerData.HeadPoint), printed by
        /// HarbingerRoomGeometryTests.HarbingerBody_AtHeadScale_SpawnsAtTheHead_AndItsRadiusIsThePinnedReachInput. Already includes
        /// scale - do not multiply by HeadScale. (Scale 1 is 0.6788, Setup.Radius, the melee-reach radius, not the 0.48 collision sphere.)
        /// </summary>
        public const float HeadRadius = 4.2087f;

        /// <summary>
        /// The hail's drop height (the rain hazard's drop_z): 0.75 m under the measured edge z -17.25 (the highest z at which all 741
        /// octagon grid points have a cell), so a 0.4 m ball (top -17.6) stays inside the cells.
        /// </summary>
        public const float HailDropZ = -18.0f;

        // ---- the wall strips (round 2 A2: acid flowing down the four walls, the Matron's wall strip) ----

        /// <summary>
        /// The wall under each alcove's doorway stands this far from the chamber centre along the outward vector. MEASURED
        /// 2026-11-18 (HarbingerWallStripGeometryTests): on the doorway's centre line and 1.7 m to either side, every z from -35.9 to
        /// -0.9 has a cell out to 15.000 m and none at 15.005 m, on all four sides; above the sill the centre line runs on through
        /// the doorway (`cells --openings=0x00BC012E`: the W doorway's portal at x 5.00, 3.33 m wide, 3.0 m tall, sill z 0).
        /// </summary>
        public const float WallDistance = 15.0f;

        /// <summary>The arm well's cells under a doorway are stacked in layers this tall from the chamber floor (HarbingerAlcove.WallCells).</summary>
        public const float WallCellLayer = 6.0f;

        /// <summary>
        /// The flow panel rows' centres, sill (z 0) to floor (z -36), at the Matron's own row pitch and first drop
        /// (<see cref="MatronLayout.WallPanelRowZ"/>: 3.3333 m rows of <see cref="MatronLayout.PanelScale"/> panels, the first
        /// centred half a row under the sill). Eleven rows, -1.6667 down to -35.0; the bottom row's last 0.67 m is under the floor
        /// (the Matron's is 1.33 m), and every centre is above it.
        /// </summary>
        public static readonly IReadOnlyList<float> WallPanelRowZ = RowsFromSill();

        /// <summary>The fog hosts' floor z: the Matron's 0.005 m over the floor (<see cref="MatronLayout.WallFogZ"/>), on this chamber's floor.</summary>
        public const float WallFogZ = FloorZ + 0.005f;

        private static IReadOnlyList<float> RowsFromSill()
        {
            var pitch = MatronLayout.WallPanelRowZ[0] - MatronLayout.WallPanelRowZ[1];
            var firstDrop = MatronLayout.SillZ - MatronLayout.WallPanelRowZ[0];
            var rows = new List<float>();

            for (var z = AlcoveZ - firstDrop; z > FloorZ; z -= pitch)
                rows.Add(z);

            return rows;
        }

        /// <summary>The well cell holding height <paramref name="z"/> under <paramref name="alcove"/>'s doorway.</summary>
        public static uint WallCell(HarbingerAlcove alcove, float z)
        {
            var layer = (int)Math.Floor((z - FloorZ) / WallCellLayer);

            if (layer < 0 || layer >= alcove.WallCells.Count)
                throw new ArgumentOutOfRangeException(nameof(z), z, $"no wall cell layer at z {z} under the {alcove.Name} doorway");

            return alcove.WallCells[layer];
        }

        /// <summary>
        /// The wall strip under an alcove's doorway, the Matron's (MatronLayout.WallStrip) on this room's measured wall: with
        /// <paramref name="mode"/> <see cref="MatronLayout.WallFxPanels"/> or <see cref="MatronLayout.WallFxBoth"/>, one room-facing
        /// <see cref="MatronData.AcidFlowWcid"/> panel per row (<see cref="WallPanelRowZ"/>) at <see cref="MatronLayout.PanelScale"/>,
        /// origin <see cref="MatronLayout.WallPanelOffset"/> in front of the wall plane, turned by <see cref="MatronLayout.WallPanelFacing"/>;
        /// then with <see cref="MatronLayout.WallFxFog"/> or <see cref="MatronLayout.WallFxBoth"/>, the acid fog hosts of
        /// <see cref="MatronLayout.AcidFogs"/>[<paramref name="fogPick"/>], <see cref="MatronLayout.WallFogOffset"/> into the room at
        /// the foot of the wall. Driven by the alcove's outward vector and its measured cells only: no branch on which alcove it is.
        /// </summary>
        public static IReadOnlyList<PinnacleProp> WallStrip(HarbingerAlcove alcove, int mode, int fogPick = 0)
        {
            if (alcove == null)
                throw new ArgumentNullException(nameof(alcove));

            if (mode < MatronLayout.WallFxPanels || mode > MatronLayout.WallFxBoth)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "wall_fx is 0 (panels), 1 (fog) or 2 (both)");

            if (fogPick < 0 || fogPick >= MatronLayout.AcidFogs.Count)
                throw new ArgumentOutOfRangeException(nameof(fogPick), fogPick, $"the fog pick is 0 to {MatronLayout.AcidFogs.Count - 1}");

            var o = alcove.Outward;
            var wall = ChamberCentre + WallDistance * o;
            var props = new List<PinnacleProp>();

            if (mode != MatronLayout.WallFxFog)
            {
                var at = wall - o * MatronLayout.WallPanelOffset;
                var facing = MatronLayout.WallPanelFacing(-o);

                foreach (var z in WallPanelRowZ)
                    props.Add(new PinnacleProp(MatronData.AcidFlowWcid, MatronData.Point(WallCell(alcove, z), at.X, at.Y, z, facing), MatronLayout.PanelScale));
            }

            if (mode != MatronLayout.WallFxPanels)
            {
                var at = wall - o * MatronLayout.WallFogOffset;
                var (script, lifts) = MatronLayout.AcidFogs[fogPick];

                foreach (var z in lifts.Select(lift => WallFogZ + lift))
                {
                    // local +Y into the room: a sheet-spawning script then spreads across the wall, not out from it (as the Matron's)
                    props.Add(new PinnacleProp(MatronData.EffectHostWcid, Point(WallCell(alcove, z), at.X, at.Y, z, YawDegFacing(-o)), Script: script));
                }
            }

            return props;
        }

        // ---- the alcove signatures (round 2 B2) ----

        /// <summary>
        /// The hazard region preset of an alcove (HazardRegionPresets.HarbingerAlcove*): "harbinger-alcove-" plus its name in
        /// lower case, so the four presets come from the alcove's own data with no branch on which alcove it is.
        /// </summary>
        public static string RegionPreset(HarbingerAlcove alcove)
            => HazardMechanics.HazardRegionPresets.HarbingerName + "-alcove-" + alcove.Name.ToLowerInvariant();

        /// <summary>
        /// The Corrosion pool slots of an alcove, in its own frame (u outward from the chamber, v along <see cref="Perp"/>,
        /// metres from the alcove centre): a 4 x 4 grid at a 2.25 m pitch (u, v in -3.375, -1.125, 1.125, 3.375) whose four
        /// corners are pulled in to (+/-3.0, +/-3.0) so their centres stand on the floor (the measured floor is every point
        /// within 4.25 m of the centre; a 3.375 corner is 4.77 m out, past the octagon), plus two sill slots at u -4.0,
        /// v +/-0.6 over the doorway sill (the doorway is on the chamber side, u -4.5 to -5.0, 3.33 m wide). With pools of at
        /// least <see cref="MinPoolSize"/> the slots' squares cover every point of the measured floor octagon (apothem 4.5) and
        /// the sill: there is no spot along the walls a pool can never reach (HarbingerSignatureTests). Index order is the
        /// grid row by row (u ascending, then v ascending), then the two sill slots.
        /// </summary>
        public static readonly IReadOnlyList<Vector2> PoolSlotsUV = BuildPoolSlots();

        /// <summary>The smallest pool square (m) for which <see cref="PoolSlotsUV"/> cover the whole alcove floor and sill.</summary>
        public const float MinPoolSize = 2.25f;

        private static IReadOnlyList<Vector2> BuildPoolSlots()
        {
            var axis = new[] { -3.375f, -1.125f, 1.125f, 3.375f };
            var slots = new List<Vector2>();

            foreach (var u in axis)
            {
                foreach (var v in axis)
                {
                    // a grid corner pulled in along its diagonal onto the floor
                    slots.Add(Math.Abs(u) > 3f && Math.Abs(v) > 3f ? new Vector2(Math.Sign(u) * 3f, Math.Sign(v) * 3f) : new Vector2(u, v));
                }
            }

            slots.Add(new Vector2(-4.0f, -0.6f));
            slots.Add(new Vector2(-4.0f, 0.6f));
            return slots;
        }

        /// <summary>Pool slot <paramref name="slot"/> of <paramref name="alcove"/> in landblock x, y: centre + u outward + v perp.</summary>
        public static Vector2 PoolSlot(HarbingerAlcove alcove, int slot)
        {
            if (alcove == null)
                throw new ArgumentNullException(nameof(alcove));

            if (slot < 0 || slot >= PoolSlotsUV.Count)
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"an alcove has {PoolSlotsUV.Count} pool slots (0-{PoolSlotsUV.Count - 1})");

            var uv = PoolSlotsUV[slot];

            return alcove.Centre + uv.X * alcove.Outward + uv.Y * Perp(alcove.Outward);
        }

        /// <summary>
        /// The ad-hoc hazard area of pool slot <paramref name="slot"/>: a <paramref name="size"/> m square centred on the slot
        /// (the outward vectors are axis-aligned, so the square stays axis-aligned), on the alcove floor
        /// (HazardRegionPresets.HarbingerAlcoveGroundZ) under its ceiling (HarbingerAlcoveCeilingZ).
        /// </summary>
        public static PinnacleHazardArea PoolArea(HarbingerAlcove alcove, int slot, double size)
        {
            var c = PoolSlot(alcove, slot);
            var h = (float)size * 0.5f;

            return new PinnacleHazardArea(c.X - h, c.X + h, c.Y - h, c.Y + h,
                HazardMechanics.HazardRegionPresets.HarbingerAlcoveGroundZ, HazardMechanics.HazardRegionPresets.HarbingerAlcoveCeilingZ);
        }

        /// <summary>The acid film's square edge (m): twice the alcove's wall distance (HazardRegionPresets.HarbingerAlcoveWallDistance), the whole floor.</summary>
        public const float FilmSize = 2.0f * HazardMechanics.HazardRegionPresets.HarbingerAlcoveWallDistance;

        /// <summary>
        /// The acid film's ad-hoc hazard area (round 4): a <see cref="FilmSize"/> m square centred on the alcove's centre, on the
        /// alcove floor (HarbingerAlcoveGroundZ) under its ceiling (HarbingerAlcoveCeilingZ), like <see cref="PoolArea"/>. Derived from
        /// the alcove's own centre, with no branch on which alcove it is.
        /// </summary>
        public static PinnacleHazardArea FilmArea(HarbingerAlcove alcove)
        {
            if (alcove == null)
                throw new ArgumentNullException(nameof(alcove));

            var h = FilmSize * 0.5f;

            return new PinnacleHazardArea(alcove.Centre.X - h, alcove.Centre.X + h, alcove.Centre.Y - h, alcove.Centre.Y + h,
                HazardMechanics.HazardRegionPresets.HarbingerAlcoveGroundZ, HazardMechanics.HazardRegionPresets.HarbingerAlcoveCeilingZ);
        }

        /// <summary>An alcove's centre in x, y: the chamber centre plus <see cref="AlcoveDistance"/> along <paramref name="outward"/>.</summary>
        public static Vector2 Centre(Vector2 outward) => ChamberCentre + AlcoveDistance * outward;

        /// <summary>The outward vector turned a quarter turn counter-clockwise (+90 degrees).</summary>
        public static Vector2 Perp(Vector2 outward) => new Vector2(-outward.Y, outward.X);

        public static Vector2 StandXY(Vector2 outward) => Centre(outward) - StandBack * outward;

        public static Vector2 CrystalXY(Vector2 outward) => Centre(outward) + CrystalOut * outward;

        /// <summary>Guard 0 on the +p side, guard 1 on the -p side.</summary>
        public static Vector2 GuardXY(Vector2 outward, int index)
            => Centre(outward) + (index == 0 ? GuardSide : -GuardSide) * Perp(outward);

        public static Vector2 PortalXY(Vector2 outward) => ChamberCentre + PortalOut * outward;

        /// <summary>Summon ring point <paramref name="index"/> (0 to 7), x, y: 45 degrees per step from due east, counter-clockwise.</summary>
        public static Vector2 SummonXY(int index)
        {
            if (index < 0 || index >= SummonRingCount)
                throw new ArgumentOutOfRangeException(nameof(index), index, "the summon ring has 8 points (0-7)");

            var angle = index * Math.PI * 2.0 / SummonRingCount;

            return new Vector2(
                (float)(ChamberCentre.X + SummonRingRadius * Math.Cos(angle)),
                (float)(ChamberCentre.Y + SummonRingRadius * Math.Sin(angle)));
        }

        /// <summary>A point turned to face <paramref name="yawDeg"/> degrees counter-clockwise from north.</summary>
        public static PinnaclePoint Point(uint cell, float x, float y, float z, float yawDeg)
        {
            var q = PuzzleGateGenerator.YawQuaternion(yawDeg);

            return new PinnaclePoint(cell, x, y, z, q.W, q.X, q.Y, q.Z);
        }

        /// <summary>The yaw (degrees CCW from north) of a body facing <paramref name="direction"/>: north (0, 1) is 0, west (-1, 0) is 90.</summary>
        public static float YawDegFacing(Vector2 direction)
        {
            var yaw = Math.Atan2(-direction.X, direction.Y) * 180.0 / Math.PI;

            return (float)(yaw < -1e-9 ? yaw + 360.0 : yaw);
        }
    }
}
