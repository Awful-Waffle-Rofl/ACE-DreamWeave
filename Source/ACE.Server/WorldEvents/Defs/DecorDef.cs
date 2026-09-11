using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One spawn-time scenery layer a source theme drops at its geometry centre when a run goes Active
    /// (TECH-DESIGN 2.4, WP-14). Decor is pure look: it is never a Creature, never counts toward LiveCount,
    /// MaxAlive, an objective or the participation ledger, and is swept with everything else at Finish.
    ///
    /// The Sky Rift stack is the first consumer, and the field meanings follow the spinning-scenery recipe
    /// (Source/.claude/skills/weenie-generator/references/spinning_scenery.md):
    ///
    ///   * the carrier Setup's idle MotionTable places the visible disc 0.455 x Scale ABOVE the object
    ///     origin, or BELOW it when the object is flipped 180 degrees about X (<see cref="Inverted"/>);
    ///   * an origin below terrain is snapped up to the surface and drags its disc with it, and the client
    ///     stops animating an object whose ORIGIN is far from the viewer - so a high stack is built from
    ///     INVERTED layers whose origins stay low and close, with the discs hanging beneath them.
    ///
    /// <see cref="Dz"/> is therefore a deliberate compound of the disc height and 0.455 x Scale. It is not
    /// derived here and must not be "corrected" against Scale.
    /// </summary>
    public class DecorDef
    {
        /// <summary>The scenery weenie to place. Must be greater than 0 or the entry is dropped at load.</summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>
        /// Metres EAST of the placement origin (WP-19). 0, the default and every spawn-time decor entry's
        /// value, is the pre-WP-19 behaviour: the object sits directly over the origin. Used by the reward
        /// cache dressing, where the offsets are measured from THE CACHE rather than the geometry centre.
        /// </summary>
        [JsonPropertyName("dx")]
        public float Dx { get; set; }

        /// <summary>Metres NORTH of the placement origin (WP-19). 0 by default - see <see cref="Dx"/>.</summary>
        [JsonPropertyName("dy")]
        public float Dy { get; set; }

        /// <summary>Metres above the anchor for the object ORIGIN - see the class remarks; not the disc height.</summary>
        [JsonPropertyName("dz")]
        public float Dz { get; set; }

        /// <summary>PropertyFloat.DefaultScale (39). Must be greater than 0 or the entry is dropped at load.</summary>
        [JsonPropertyName("scale")]
        public float Scale { get; set; }

        /// <summary>
        /// Idle-cycle playback rate: PropertyFloat.MotionSpeed (9007) and the create packet's
        /// CurrentMotionState.MotionState.ForwardSpeed. Must be greater than 0 or the entry is dropped.
        /// </summary>
        [JsonPropertyName("speed")]
        public float Speed { get; set; }

        /// <summary>
        /// True places the object flipped 180 degrees about X (rotation w=0, x=1, y=0, z=0), which puts the
        /// disc BELOW the origin. False is the identity rotation and the disc sits above it.
        /// </summary>
        [JsonPropertyName("inverted")]
        public bool Inverted { get; set; }

        /// <summary>
        /// Degrees about +Z (up). Identity is 0, which is the WP-14 behaviour and therefore what a theme
        /// that omits the key gets. Positive turns counter-clockwise seen from above, the same sense as
        /// <see cref="WorldEventSpawner.NpcRotation"/>.
        ///
        /// It composes with <see cref="Inverted"/> in one fixed order - yaw FIRST, then the 180 about the
        /// world X axis - which is pinned by
        /// WorldEventSpawnerDecorTests.DecorRotation_Yaw90Inverted_IsFlipTimesYaw rather than by this
        /// sentence. Yaw is what makes the Weave Spiral read as four arms from one wcid: four copies of the
        /// same rig at 0/90/180/270.
        ///
        /// Any finite value is legal and nothing is normalised into [0, 360); only NaN/Infinity drops the
        /// entry at load.
        /// </summary>
        [JsonPropertyName("yaw")]
        public float Yaw { get; set; }

        /// <summary>
        /// Degrees about the object's LOCAL X axis, applied FIRST - before <see cref="Yaw"/>, before
        /// <see cref="Inverted"/> (WP-21). 0, the default and every pre-WP-21 theme's value, reproduces the
        /// old behaviour exactly: a flat XY quad spinning about its local Z. A theme that needs the disc
        /// standing UPRIGHT (the Element Portal) sets this to 90.
        ///
        /// Any finite value is legal and nothing is normalised into [0, 360); only NaN/Infinity drops the
        /// entry at load.
        /// </summary>
        [JsonPropertyName("pitch")]
        public float Pitch { get; set; }

        /// <summary>
        /// Terrain-snap the origin, then Dz above the ground - for ground scenery; sky discs leave it
        /// false (WP-23). False, the default, reproduces every pre-WP-23 theme's behaviour byte for byte:
        /// the origin sits at centre.Z + Dz with no ground contact. True mirrors PlaceNpcs exactly -
        /// AdjustMapCoords overwrites Z with the terrain height under the (dx, dy)-offset point, then Dz is
        /// re-applied on top of that snapped ground - so it means "metres above the ground here" rather
        /// than "metres above the anchor's Z", the same distinction NpcDef.Dz draws for creatures. Indoor
        /// positions are left untouched either way, since AdjustMapCoords only makes sense outdoors.
        /// </summary>
        [JsonPropertyName("snap")]
        public bool Snap { get; set; }
    }
}
