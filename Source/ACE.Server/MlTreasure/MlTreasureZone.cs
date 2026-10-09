namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// One of Marae Lassel's three level zones (Docs/Marae-Lassel/BESTIARY.md): South (rungs 185-205),
    /// North (215-225), Plateau (240-275). A treasure map found in one zone must lead to a dig site in
    /// the SAME zone, and the digsite encounter opened at that site must draw creatures from that zone
    /// only (owner-approved design, zone isolation).
    ///
    /// Unknown is the "no zone data" value, never a fourth real zone: every zone-aware lookup treats it as
    /// NO FILTER, which is what keeps a 9-column (pre-zone) dig-sites.tsv, or a map dropped before this
    /// property existed, working exactly as it did before zone isolation shipped.
    /// </summary>
    public enum MlTreasureZone
    {
        Unknown = 0,
        South = 1,
        North = 2,
        Plateau = 3,
    }

    /// <summary>
    /// Pure helpers over <see cref="MlTreasureZone"/>. No PropertyManager, no I/O - safe under unit test.
    /// </summary>
    public static class MlTreasureZones
    {
        /// <summary>Parses the catalogue's zone column text ("south"/"north"/"plateau", case-insensitive).
        /// Anything else - missing, blank, unrecognised - is <see cref="MlTreasureZone.Unknown"/>, never a
        /// skipped row: the row is still a usable site, just an unzoned one.</summary>
        public static MlTreasureZone Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return MlTreasureZone.Unknown;

            switch (text.Trim().ToLowerInvariant())
            {
                case "south": return MlTreasureZone.South;
                case "north": return MlTreasureZone.North;
                case "plateau": return MlTreasureZone.Plateau;
                default: return MlTreasureZone.Unknown;
            }
        }

        /// <summary>The catalogue column spelling for a zone ("south"/"north"/"plateau"/""), the inverse of
        /// <see cref="Parse"/>.</summary>
        public static string ToColumnText(MlTreasureZone zone)
        {
            switch (zone)
            {
                case MlTreasureZone.South: return "south";
                case MlTreasureZone.North: return "north";
                case MlTreasureZone.Plateau: return "plateau";
                default: return "";
            }
        }

        /// <summary>
        /// The zone a creature's authored level belongs to, by the BESTIARY.md level bands: South tops out
        /// at rung 205, North at 225, Plateau runs 240-275. The boundary rule is "&lt;= 210 South, &lt;= 230
        /// North, else Plateau" - the midpoints between each zone's top rung and the next zone's bottom rung
        /// (205/215 -> 210, 225/240 -> 230) - so every authored rung in every zone round-trips to its own
        /// zone with room either side.
        /// </summary>
        public static MlTreasureZone FromLevel(int level)
        {
            if (level <= 210)
                return MlTreasureZone.South;

            if (level <= 230)
                return MlTreasureZone.North;

            return MlTreasureZone.Plateau;
        }
    }
}
