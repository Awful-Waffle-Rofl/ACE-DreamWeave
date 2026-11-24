using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

// World Events Hold: the pure rules of the knights' health wisps (protect.healthWisps) - how many to show for a health reading,
// and where the row hangs. No clock, no world, no settings (D6).
namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        /// <summary>Percentage points a wisp must be cleared by before it comes BACK after going out, so health hovering on a threshold does not flicker.</summary>
        public const double HealthWispRestoreHysteresisPercent = 1.0;

        /// <summary>
        /// How many of <paramref name="n"/> wisps to show for <paramref name="cur"/> of <paramref name="max"/> health. Wisp k (1-based)
        /// shows while health is strictly above 100 * (k - 1) / n percent (with 4: 4 above 75%, 3 above 50%, 2 above 25%, 1 above 0).
        /// A wisp already shown (k at or below <paramref name="previouslyShown"/>) goes out exactly at its threshold; one that was
        /// out comes back only once health is 1% above it. Any health above 0 shows at least one; dead (0 or less) or no max shows none.
        /// </summary>
        public static int HealthWispsShown(double cur, double max, int n, int previouslyShown)
        {
            if (n <= 0 || !(max > 0) || !(cur > 0))
                return 0;

            var percent = Math.Min(100.0, cur / max * 100.0);
            var shown = 0;

            for (var k = 1; k <= n; k++)
            {
                var threshold = 100.0 * (k - 1) / n;
                var needed = k <= previouslyShown ? threshold : threshold + HealthWispRestoreHysteresisPercent;

                if (percent > needed)
                    shown = k;
            }

            return Math.Max(1, shown);
        }

        /// <summary>
        /// The wisp row for one knight (pure): <paramref name="def"/>.Count wisps spaced <paramref name="def"/>.Spacing metres apart,
        /// centred over <paramref name="target"/>'s start spot on the line perpendicular to its facing, <paramref name="def"/>.Dz
        /// metres above its ground spot. Offsets are from the run's anchor (the same frame as <see cref="PlaceTargetDecor"/>, which
        /// does the rotation) and the entries are ground-snapped. Index 0 is the last wisp to go out: the
        /// HIGHEST index goes out first.
        /// </summary>
        public static List<DecorDef> HealthWispOffsets(RingOffset target, HealthWispsDef def)
        {
            var row = new List<DecorDef>();

            if (def == null || def.Wcid == 0 || def.Count <= 0)
                return row;

            for (var i = 0; i < def.Count; i++)
            {
                row.Add(new DecorDef
                {
                    Wcid = def.Wcid,
                    Dx = (float)((i - (def.Count - 1) / 2.0) * def.Spacing),
                    Dy = 0f,
                    Dz = (float)def.Dz,
                    Scale = (float)def.Scale,
                    Speed = 1f,
                    Snap = true
                });
            }

            return PlaceTargetDecor(new[] { target }, row);
        }
    }
}
