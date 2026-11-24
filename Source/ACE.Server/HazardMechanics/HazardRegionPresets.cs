using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Named regions authored ahead of time. A preset is instance-free; <see cref="TryGet"/> stamps the caller's
    /// instance. The /hazard command refuses a preset whose landblock is not the caller's.
    ///
    /// The Pinnacle boss room is ONE preset (owner ruling: a generic library, the Pinnacle is a consumer).
    /// </summary>
    public static class HazardRegionPresets
    {
        public const string PinnacleName = "pinnacle";

        /// <summary>The Pinnacle wisps' height above the ground plane (their origin is at their feet; see <see cref="Pinnacle"/>).</summary>
        public const float PinnacleWispOffset = 0.5f;

        /// <summary>
        /// The Pinnacle boss arena, dungeon landblock 0x0069. Ground plane -47.75, ceiling face -42.05.
        ///
        /// The rectangle is the architect's x -4.6..34.6, y -34.6..-5.4, which sits inside the ward room faces as
        /// moved on 2026-10-06 (x -4.75..34.75, y -34.75..-5.25).
        ///
        /// Geometry (recon 2026-10-06, `cells` in 0x0069): pit floor -54.00; three solids with tops at -48.00 -
        /// west block x 5..15 y -25..-15 (cell 0x00690113), a thin centre bridge cap x 15..25 y -21.75..-18.25
        /// (cell 0x00690106, hollow cells below it at -54), east block x 25..35 y -25..-15 (cell 0x00690119).
        /// The pit is covered by invisible solid floor plates with tops at -47.75 (the ground plane).
        ///
        /// WISPS GLIDE ABOVE THE FLOOR (owner ruling 2026-10-18; they used to fly at -51 in the pit, which put every
        /// formation point over a block inside the solid, so PlaceWisp stopped the wisp at the block's base while its
        /// pillar fired on the block top - the telegraph far from the burst). No fixed hazard plane: wisps fly at
        /// ground + <see cref="PinnacleWispOffset"/>. The red wisp setup 0x020009DB has its ORIGIN AT ITS FEET
        /// (collision spheres r 0.48 centred 0.48 and 1.35 above the origin, top 1.83 at scale 1; `cells
        /// --modelbounds=0x020009DB`), so the body spans -47.25..-45.42: 0.5 m clear of the plates and the block tops
        /// (-48.00), 3.37 m under the ceiling face (-42.05). WispRules.Validate refuses a scale whose body would come
        /// within 0.1 m of that ceiling: (5.20 - 0.10) / 1.83 = 2.79, so scale 2.78 is the largest it names. Every point of the rect at that z lies in an EnvCell, so
        /// PlaceWisp finds no solid and Stand equals Pillar (PinnacleArenaGeometryTests). The band still reaches the
        /// pit floor so nobody standing in the pit is safe.
        /// </summary>
        public static HazardRegion Pinnacle(uint instance) => HazardRegion.Rect(
            PinnacleName, 0x0069, instance, false,
            -4.6f, 34.6f, -34.6f, -5.4f,
            HazardGroundMode.Plane,
            groundPlaneZ: -47.75f,
            hazardPlaneZ: null, // wisps fly at ground + PinnacleWispOffset (owner ruling 2026-10-18), not in the pit.
            hazardOffset: PinnacleWispOffset,
            ceilingZ: -42.05f,
            bandBelow: 6.5f, // ground -47.75 - 6.5 = -54.25, just under the pit floor -54.00 (no safe spots).
            bandAbove: 5.7f); // ground -47.75 + 5.7 = the ceiling face -42.05.

        public const string MatronName = "matron";

        /// <summary>
        /// The Olthoi Matron's hive chamber, landblock 0x586B (the Queen's Quarters Knot copy): a flat floor at z -60.0
        /// under a z -38 ceiling. The rectangle x 195..245, y -165..-105 is the room's bounding box (a cross: core x
        /// 205-235 y -145..-105, wings x 195-205 and 235-245 y -135..-115, south nub x 215-225 y -165..-145); the fill's
        /// sheet tiles outside the cross land in other cells at z -60 or none (plan risk, owner probe). The band runs
        /// from 0.5 m under the floor to the ceiling (-60 + 22 = -38) so nobody standing in the room is safe.
        /// </summary>
        public static HazardRegion Matron(uint instance) => HazardRegion.Rect(
            MatronName, 0x586B, instance, false,
            195f, 245f, -165f, -105f,
            HazardGroundMode.Plane,
            groundPlaneZ: -60.0f,
            hazardPlaneZ: null,
            hazardOffset: PinnacleWispOffset,
            ceilingZ: -38.0f,
            bandBelow: 0.5f,
            bandAbove: 22.0f);

        public const string HarbingerName = "harbinger";

        /// <summary>
        /// The Drift Harbinger's lair floor, landblock 0x00BC (the Knot copy): the 30 x 30 m octagon chamber, rect x 5..35,
        /// y -35..-5, a flat floor at z -36.0 under a ceiling face at z -11.7 (the centre platform's top is -11.2). The band
        /// runs from 0.5 m under the floor to 20 m over it (-36.5..-16.0), so nobody standing in the chamber is safe and the
        /// four alcoves (z 0), 36 m up, are never in it. There is no platform region: the platform is blocked space (owner
        /// ruling 2026-11-15). HarbingerLayoutTests pins every value.
        /// </summary>
        public static HazardRegion Harbinger(uint instance) => HazardRegion.Rect(
            HarbingerName, 0x00BC, instance, false,
            5f, 35f, -35f, -5f,
            HazardGroundMode.Plane,
            groundPlaneZ: -36.0f,
            hazardPlaneZ: null,
            hazardOffset: PinnacleWispOffset,
            ceilingZ: -11.7f,
            bandBelow: 0.5f,
            bandAbove: 20.0f);

        // ---- the four Harbinger alcoves (landblock 0x00BC, the Knot copy) ----
        //
        // MEASURED 2026-11-18 (`ACE.Content.Tools cells --landblock=0x00BC` --rooms / --room=0xCELL --cells / --openings=0xCELL /
        // --sample=0.25, plus the ported-physics AdjustCell.GetCell column probe HarbingerAlcoveRegionTests repeats):
        //   - each alcove is its own two-cell room (W 0x00BC012D+0139, N 0130+013B, S 0134+013E, E 0137+0140), floor z 0.00,
        //     68.6 m2, centred 20 m out from the chamber centre (20, -20) along its compass axis;
        //   - every point within 4.25 m of the centre has floor (0.25 m grid); the walls stand 4.5 m out (the doorway plane);
        //   - the floor cell meets the upper cell at z 6.00 and the cell column ends at z 9.00 everywhere within 4.25 m: the
        //     ceiling face;
        //   - one opening each, 3.33 m wide x 3.00 m tall from the sill (z 0), 4.5 m from the centre on the chamber side
        //     (N: 0x00BC0130 -> 0131 at (20.00, -4.50), normal (0, -1)).
        //   - the floor is an octagon of apothem 4.5 (vertices about 4.87 m out; the farthest 0.25 m floor sample is 4.80 m),
        //     and each doorway's sill cell (W 012E, N 0131, S 0135, E 0138) is floor out to 5.0 m, its corners about 5.27 m.
        // Each preset PLACES hazards in a circle of HarbingerAlcoveRadius (4.0, 0.5 m inside the walls, so a ball or a burst
        // fits) but counts a PLAYER inside out to HarbingerAlcovePlayerRadius (5.3, HazardRegion.PlayerMargin): every floor
        // point, the sill included, is in the region, so there is no safe ring along the wall. Ground z 0, ceiling 9.0, and a
        // band from 0.5 m under the sill to the ceiling. The names sort after "harbinger", so ForLandblock(0x00BC) stays
        // "harbinger".

        public const string HarbingerAlcoveWestName = "harbinger-alcove-w";
        public const string HarbingerAlcoveNorthName = "harbinger-alcove-n";
        public const string HarbingerAlcoveSouthName = "harbinger-alcove-s";
        public const string HarbingerAlcoveEastName = "harbinger-alcove-e";

        /// <summary>The alcove floor (the sill), measured.</summary>
        public const float HarbingerAlcoveGroundZ = 0.0f;

        /// <summary>The alcove ceiling face: the top of the alcove's cell column, measured.</summary>
        public const float HarbingerAlcoveCeilingZ = 9.0f;

        /// <summary>The preset circle's radius: 0.5 m inside the measured walls (4.5 m), every point of it on floor.</summary>
        public const float HarbingerAlcoveRadius = 4.0f;

        /// <summary>
        /// The radius a PLAYER counts as inside out to (HazardRegion.PlayerMargin = this - HarbingerAlcoveRadius): past the
        /// floor's farthest point, the doorway sill's corners at about 5.27 m. HarbingerAlcoveRegionTests proves every floor
        /// point of every alcove and sill is within it.
        /// </summary>
        public const float HarbingerAlcovePlayerRadius = 5.3f;

        /// <summary>The doorway's measured width (`cells --openings`, 3.33 m): the fire alcove's lane exclusion is this wide.</summary>
        public const float HarbingerAlcoveDoorwayWidth = 3.33f;

        /// <summary>The doorway plane's measured distance from the alcove centre, and the far wall's.</summary>
        public const float HarbingerAlcoveWallDistance = 4.5f;

        public static HazardRegion HarbingerAlcoveWest(uint instance) => HarbingerAlcove(HarbingerAlcoveWestName, 0f, -20f, instance);

        /// <summary>
        /// The north alcove: the FIRE alcove (owner mapping 2026-11-15, HarbingerData). Round 4 (owner playtest 2026-11-19): it
        /// carries no exclusion any more. Rounds 2 and 3 kept a doorway-wide lane clear of ball placement so a player standing
        /// on the line from the door to the crystal was never touched, which confined Ember roll to north-south lanes; the owner
        /// asked for balls rolling in every direction, so the safe doorway line is dropped and Ember roll draws axis=any.
        /// </summary>
        public static HazardRegion HarbingerAlcoveNorth(uint instance) => HarbingerAlcove(HarbingerAlcoveNorthName, 20f, 0f, instance);

        public static HazardRegion HarbingerAlcoveSouth(uint instance) => HarbingerAlcove(HarbingerAlcoveSouthName, 20f, -40f, instance);

        public static HazardRegion HarbingerAlcoveEast(uint instance) => HarbingerAlcove(HarbingerAlcoveEastName, 40f, -20f, instance);

        private static HazardRegion HarbingerAlcove(string name, float cx, float cy, uint instance, IReadOnlyList<HazardExclusion> exclusions = null)
            => HazardRegion.Circle(
                name, 0x00BC, instance, false,
                cx, cy, HarbingerAlcoveRadius,
                HazardGroundMode.Plane,
                groundPlaneZ: HarbingerAlcoveGroundZ,
                hazardPlaneZ: null,
                hazardOffset: PinnacleWispOffset,
                ceilingZ: HarbingerAlcoveCeilingZ,
                bandBelow: 0.5f,
                bandAbove: HarbingerAlcoveCeilingZ - HarbingerAlcoveGroundZ,
                exclusions: exclusions,
                playerMargin: HarbingerAlcovePlayerRadius - HarbingerAlcoveRadius);

        private static readonly Dictionary<string, Func<uint, HazardRegion>> presets =
            new Dictionary<string, Func<uint, HazardRegion>>(StringComparer.OrdinalIgnoreCase)
            {
                { PinnacleName, Pinnacle },
                { MatronName, Matron },
                { HarbingerName, Harbinger },
                { HarbingerAlcoveWestName, HarbingerAlcoveWest },
                { HarbingerAlcoveNorthName, HarbingerAlcoveNorth },
                { HarbingerAlcoveSouthName, HarbingerAlcoveSouth },
                { HarbingerAlcoveEastName, HarbingerAlcoveEast },
            };

        public static IReadOnlyList<string> Names => presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        public static bool TryGet(string name, uint instance, out HazardRegion region)
        {
            region = null;

            if (name == null || !presets.TryGetValue(name.Trim(), out var factory))
                return false;

            region = factory(instance);
            return true;
        }

        /// <summary>The first preset authored for <paramref name="landblock"/>, or null. Lets /hazard default to it there.</summary>
        public static string ForLandblock(ushort landblock)
        {
            foreach (var name in Names)
                if (presets[name](0).Landblock == landblock)
                    return name;

            return null;
        }
    }
}
