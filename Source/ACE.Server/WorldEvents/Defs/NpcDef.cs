using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One spawn-time NPC a source theme places around its geometry centre when a run goes Active
    /// (WP-15, first consumer the Weave Spiral's three Loz attendants).
    ///
    /// An npc IS a Creature - unlike <see cref="DecorDef"/> scenery - so it takes the creature placement
    /// recipe (terrain snap, Creature guard) but NONE of the creature bookkeeping: it is never charged
    /// against MaxAlive, never appears in LiveCount/LiveTotal, never enters an objective or the
    /// participation ledger, and carries no P_WorldEvent back-reference, so its death cannot reach
    /// Creature.Die's world-event hook (Source/ACE.Server/WorldObjects/Creature_Death.cs:130 requires BOTH
    /// that back-reference and the WorldEventId stamp). It is swept with everything else at Finish.
    ///
    /// The weenies themselves ship non-attackable; nothing here depends on that either way.
    /// </summary>
    public class NpcDef
    {
        /// <summary>The NPC weenie to place. Must be greater than 0 or the entry is dropped at load.</summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>Metres EAST of the theme geometry centre. Negative is west.</summary>
        [JsonPropertyName("dx")]
        public float Dx { get; set; }

        /// <summary>Metres NORTH of the theme geometry centre. Negative is south.</summary>
        [JsonPropertyName("dy")]
        public float Dy { get; set; }

        /// <summary>
        /// Metres ABOVE the ground the npc is snapped to, not above the centre's Z: the placement path
        /// snaps the npc to terrain first and then re-applies this offset, because an npc is a creature and
        /// must stand on the ground. 0 for every shipped entry.
        /// </summary>
        [JsonPropertyName("dz")]
        public float Dz { get; set; }

        /// <summary>
        /// The heading the npc FACES, in degrees about +Z (up), turned into a quaternion by
        /// <see cref="WorldEventSpawner.NpcRotation"/> as (w=cos(yaw/2), x=0, y=0, z=sin(yaw/2)).
        ///
        /// Yaw 0 faces +Y, which is NORTH. That is read off the engine, not assumed: a Position's facing is
        /// <c>Vector3.Transform(Vector3.UnitY, Rotation)</c> (Source/ACE.Entity/Position.cs:102-105,
        /// Position.GetCurrentDir), so the identity rotation faces +Y and this quaternion rotates that
        /// vector counter-clockwise seen from above. Yaw 90 therefore faces -X, WEST.
        ///
        /// For an npc standing at polar angle a (degrees, from +X counter-clockwise) that should face the
        /// centre, yaw is a + 90. The three shipped Weave Spiral values (90, 210, 330) are exactly the three
        /// headings reviewed live on 2026-08-16; they are stored explicitly rather than recomputed from
        /// Dx/Dy so a scene can point an npc anywhere.
        ///
        /// Any finite value is legal and nothing is normalised into [0, 360); only NaN/Infinity drops the
        /// entry at load.
        /// </summary>
        [JsonPropertyName("yaw")]
        public float Yaw { get; set; }
    }
}
