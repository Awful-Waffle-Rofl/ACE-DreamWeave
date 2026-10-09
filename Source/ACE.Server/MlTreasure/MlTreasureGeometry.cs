using System.Numerics;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// Pure map-coordinate math for the ML Treasure Hunt reveal (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md
    /// section 6). No world state, no PropertyManager reads - every input is a plain value so this is
    /// unit-testable without a live server. Distance is measured entirely in map-coordinate space: the
    /// caller passes two Vector2s already in the X=EastWest, Y=NorthSouth axis order that
    /// PositionExtensions.GetMapCoords and MlTreasureSiteStore.Site.MapCoords both use. Never reconstruct
    /// a Position from a stored map coordinate to measure distance - GetMapCoords and
    /// new Position(Vector2) are not exact inverses and the round trip drifts about 12 m.
    /// </summary>
    public static class MlTreasureGeometry
    {
        /// <summary>1 map unit = 240 metres (PositionExtensions.GetMapCoords / SurfaceDigSitesCommand.ToMapCoords).</summary>
        public const float MetresPerMapUnit = 240f;

        /// <summary>The three reveal stages a Use can land on, TREASURE-HUNT-PLAN.md section 6.</summary>
        public enum Stage
        {
            /// <summary>Beyond the far threshold: name the nearest town plus a rough bearing only.</summary>
            Far,
            /// <summary>Between the far and near thresholds: a cardinal bearing only.</summary>
            Cardinal,
            /// <summary>Within the near threshold: dig.</summary>
            Dig
        }

        /// <summary>Map-coordinate delta (player minus site) converted to metres. Pure vector subtraction
        /// and a scalar multiply - no map-space assumption beyond "both inputs are map coordinates".</summary>
        public static float DistanceMetres(Vector2 playerMapCoords, Vector2 siteMapCoords)
        {
            return (playerMapCoords - siteMapCoords).Length() * MetresPerMapUnit;
        }

        /// <summary>Classifies a distance already in metres against the two configured thresholds.
        /// farMetres and nearMetres are caller-supplied (tunables live in PropertyManager, which this
        /// class never touches) - nearMetres is expected to be less than farMetres, but a caller that
        /// passes them reversed still gets a defined answer (nothing here throws).</summary>
        public static Stage Classify(float distanceMetres, float farMetres, float nearMetres)
        {
            if (distanceMetres > farMetres)
                return Stage.Far;

            if (distanceMetres > nearMetres)
                return Stage.Cardinal;

            return Stage.Dig;
        }

        /// <summary>
        /// An 8-point compass bearing FROM the player TOWARD the site, in map-coordinate space (X=East,
        /// Y=North). Ties resolve toward the earlier-listed direction (e.g. a perfect diagonal reads as
        /// an ordinal, and a perfectly cardinal delta reads as that cardinal). A zero vector (player
        /// exactly at the site) reads as "north" - Classify would have already routed that case to Dig
        /// in every real caller, so this is a defined-but-unreached default rather than a meaningful
        /// answer.
        /// </summary>
        public static string Bearing8(Vector2 playerMapCoords, Vector2 siteMapCoords)
        {
            var delta = siteMapCoords - playerMapCoords;

            if (delta.X == 0 && delta.Y == 0)
                return "north";

            // atan2(x, y) so that a pure +Y (north) delta reads as angle 0, going clockwise through
            // +X (east) at 90 degrees - the usual compass-bearing convention.
            var angleDegrees = System.Math.Atan2(delta.X, delta.Y) * (180.0 / System.Math.PI);

            if (angleDegrees < 0)
                angleDegrees += 360.0;

            // 8 sectors of 45 degrees, centred on each cardinal/ordinal direction.
            var index = (int)System.Math.Round(angleDegrees / 45.0) % 8;

            switch (index)
            {
                case 0: return "north";
                case 1: return "northeast";
                case 2: return "east";
                case 3: return "southeast";
                case 4: return "south";
                case 5: return "southwest";
                case 6: return "west";
                default: return "northwest";
            }
        }
    }
}
