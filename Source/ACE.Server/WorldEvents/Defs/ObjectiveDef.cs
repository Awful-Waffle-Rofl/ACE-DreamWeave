using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One fixed-offset objective (Source) spawn a theme places around its geometry centre when a run goes
    /// Active (WP-21). When a theme lists ANY entries here, they REPLACE the anchor-following
    /// <see cref="SourceThemeDef.ObjectiveWcid"/> placement entirely - the Element Portal's two flanking
    /// pillars need fixed offsets a randomly-rotated/jittered ring anchor cannot give (WorldEventGeometry
    /// rotates and jitters every ring/edges/disc anchor).
    ///
    /// An objective IS a Creature and takes the Source spawn kind - MaxAlive/wave pressure exempt, but
    /// counted toward DestroySourceObjective exactly like an anchor-placed objective.
    /// </summary>
    public class ObjectiveDef
    {
        /// <summary>The objective weenie to place. Must be greater than 0 or the entry is dropped at load.</summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>Metres EAST of the theme geometry centre. Negative is west.</summary>
        [JsonPropertyName("dx")]
        public float Dx { get; set; }

        /// <summary>Metres NORTH of the theme geometry centre. Negative is south.</summary>
        [JsonPropertyName("dy")]
        public float Dy { get; set; }

        /// <summary>
        /// Metres ABOVE the ground the objective is snapped to, not above the centre's Z - the same rule as
        /// NpcDef.Dz. Unlike an npc, an objective takes the shared Source placement path (Spawn ->
        /// PlaceOnLandblock -> TryPlace), which snaps outdoors via AdjustMapCoords and would otherwise
        /// silently discard this value; WorldEventSpawner.SpawnObjectives threads it through explicitly
        /// (dzByAnchor) so TryPlace re-applies it on top of the snapped ground via ObjectiveSnapZ, exactly
        /// as PlaceNpcs re-applies NpcDef.Dz (WP-21).
        /// </summary>
        [JsonPropertyName("dz")]
        public float Dz { get; set; }

        /// <summary>
        /// The heading the objective FACES, in degrees about +Z (up), the same sense as NpcDef.Yaw. Any
        /// finite value is legal and nothing is normalised into [0, 360); only NaN/Infinity drops the entry
        /// at load.
        /// </summary>
        [JsonPropertyName("yaw")]
        public float Yaw { get; set; }
    }
}
