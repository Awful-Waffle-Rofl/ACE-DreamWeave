using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// Which region one Pinnacle hazard run uses (LivePinnacleWorld.StartHazard). Pure: no world, no settings.
    /// </summary>
    public static class PinnacleHazardRegions
    {
        /// <summary>The name an ad-hoc <see cref="PinnacleHazardArea"/> region carries (logs and /hazard describe).</summary>
        public const string AreaName = "pinnacle-area";

        /// <summary>
        /// The region for one run, stamped with <paramref name="instance"/>, or null with <paramref name="error"/>:
        /// <list type="bullet">
        /// <item>options.Area set: a HazardRegion.Rect built from it, in the landblock of the fight's preset
        /// (<paramref name="fightPreset"/>, which must resolve), with that preset's indoor/outdoor flag and hazard offset;</item>
        /// <item>else options.Region set: that preset (HazardRegionPresets), which must exist and be on the fight preset's landblock;</item>
        /// <item>else the fight's preset.</item>
        /// </list>
        /// Both Region and Area set is refused: a script means one or the other, and guessing would put a hazard somewhere
        /// it did not ask for.
        /// </summary>
        public static HazardRegion Resolve(PinnacleHazardOptions options, string fightPreset, uint instance, out string error)
        {
            error = null;

            if (options?.Area != null && options.Region != null)
            {
                error = $"hazard options name both a region preset '{options.Region}' and an ad-hoc area; pass one";
                return null;
            }

            if (!HazardRegionPresets.TryGet(fightPreset, instance, out var fightRegion))
            {
                error = $"no hazard region preset '{fightPreset}'";
                return null;
            }

            if (options?.Area != null)
            {
                var a = options.Area;

                return HazardRegion.Rect(AreaName, fightRegion.Landblock, instance, fightRegion.Outdoor,
                    a.MinX, a.MaxX, a.MinY, a.MaxY,
                    HazardGroundMode.Plane,
                    groundPlaneZ: a.GroundZ,
                    hazardPlaneZ: null,
                    hazardOffset: fightRegion.HazardOffset,
                    ceilingZ: a.CeilingZ,
                    bandBelow: a.BandBelow,
                    bandAbove: a.BandAbove);
            }

            if (options?.Region != null)
            {
                if (!HazardRegionPresets.TryGet(options.Region, instance, out var named))
                {
                    error = $"no hazard region preset '{options.Region}'";
                    return null;
                }

                if (named.Landblock != fightRegion.Landblock)
                {
                    error = $"hazard region preset '{options.Region}' is on landblock 0x{named.Landblock:X4}, not the fight's 0x{fightRegion.Landblock:X4}";
                    return null;
                }

                return named;
            }

            return fightRegion;
        }
    }
}
