namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// Pure guard logic for /rrtm (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Reroll"), split
    /// out of TreasureMapRerollCommands so the decidable parts are unit-testable without a live Player -
    /// ACE.Server.Tests cannot construct one (the constructor reaches DatabaseManager.Authentication),
    /// and PropertyManager reads throw under unit test. Only the two guards that are plain data live
    /// here: whether this map instance already used its one reroll (PropertyBool.TreasureMapRerolled),
    /// and whether the player is close enough to the map's currently stored site. The indoor check, the
    /// Marae Lassel box check, and the per-character cooldown all read live state and stay in the
    /// command handler.
    /// </summary>
    public static class MlTreasureRerollGuards
    {
        /// <summary>True when a map already carrying PropertyBool.TreasureMapRerolled may NOT be
        /// rerolled again - one reroll per map (TREASURE-HUNT-PLAN.md section 6, owner decision: the
        /// originally rolled site stays measurable).</summary>
        public static bool IsAlreadyRerolled(bool rerolledFlag) => rerolledFlag;

        /// <summary>
        /// True when the player is farther from the map's currently stored site than the configured
        /// maximum - the anti-abuse guard that keeps /rrtm a nudge rather than a teleport. Distance and
        /// max are both already in metres (map-coordinate delta * MlTreasureGeometry.MetresPerMapUnit),
        /// matching TreasureMapHandler's distance convention - never reconstruct a Position to measure
        /// this.
        /// </summary>
        public static bool IsTooFarToReroll(float distanceMetres, float maxMetres) => distanceMetres > maxMetres;
    }
}
