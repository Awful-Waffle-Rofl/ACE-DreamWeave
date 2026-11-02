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

        private static readonly Dictionary<string, Func<uint, HazardRegion>> presets =
            new Dictionary<string, Func<uint, HazardRegion>>(StringComparer.OrdinalIgnoreCase)
            {
                { PinnacleName, Pinnacle },
                { MatronName, Matron },
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
