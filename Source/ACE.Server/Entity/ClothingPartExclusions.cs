using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Parses PropertyString.ClothingPartExclusions: a comma-separated list of decimal body-part
    /// indices a worn item's ClothingTable must NOT apply. See the property's own doc comment
    /// (Source/ACE.Entity/Enum/Properties/PropertyString.cs) and Creature.CalculateObjDesc for how
    /// the result is used.
    /// </summary>
    public static class ClothingPartExclusions
    {
        /// <summary>
        /// Splits value on ',', trims each token, and keeps the ones that parse as an int in 0..255.
        /// Null, empty, whitespace-only, and unparseable/out-of-range tokens are ignored - never
        /// thrown on. Duplicates collapse via the HashSet.
        /// </summary>
        public static HashSet<byte> Parse(string value)
        {
            var result = new HashSet<byte>();

            if (string.IsNullOrWhiteSpace(value))
                return result;

            foreach (var token in value.Split(','))
            {
                var trimmed = token.Trim();

                if (int.TryParse(trimmed, out var parsed) && parsed >= 0 && parsed <= 255)
                    result.Add((byte)parsed);
            }

            return result;
        }

        /// <summary>
        /// End (exclusive) of the wearer's own base-appearance sub-palette region, in /8 offset units:
        /// hair at offset 0x18 length 0x8, skin at offset 0x0 length 0x18, eyes at offset 0x20 length
        /// 0x8 (Source/ACE.Server/WorldObjects/WorldObject_Networking.cs:1054,1062,1070) - together
        /// [0x0, 0x28).
        /// </summary>
        public const ushort BaseAppearancePaletteEnd = 0x28;

        /// <summary>
        /// Clips a sub-palette range so it never touches the wearer's own base-appearance region
        /// [0, BaseAppearancePaletteEnd). An item that carries a non-empty ClothingPartExclusions set
        /// hands body parts back to the wearer, and their skin/hair/eye colors belong with them - a
        /// range is clipped to keep only the part at or above BaseAppearancePaletteEnd, never dropped
        /// wholesale, so a range that legitimately extends past the region still recolors what it
        /// covers there. A range that does not overlap the region at all is returned unchanged.
        /// </summary>
        /// <returns>false when nothing of the range remains after clipping (skip it entirely).</returns>
        public static bool TryClipBaseAppearance(ushort offset, ushort length, out ushort clippedOffset, out ushort clippedLength)
        {
            clippedOffset = 0;
            clippedLength = 0;

            if (length == 0)
                return false;

            int start = offset;
            int end = offset + length; // exclusive

            if (start < BaseAppearancePaletteEnd)
                start = BaseAppearancePaletteEnd;

            if (start >= end)
                return false;

            clippedOffset = (ushort)start;
            clippedLength = (ushort)(end - start);
            return true;
        }

        /// <summary>
        /// Intersects a sub-palette range [offset, offset + length) (in /8 units) with the set of units
        /// an item's visible textures actually index, and returns what survives as contiguous runs in
        /// ascending order. A range whose units are all needed comes back as the single original run;
        /// a range none of whose units are needed comes back empty (the caller emits nothing).
        ///
        /// Used for items carrying a non-empty ClothingPartExclusions set: their ClothingTable (and
        /// weenie palette rows) were authored for the whole outfit including the excluded parts, so
        /// ranges that only colored those parts would otherwise recolor whatever the wearer puts there
        /// instead - a helm on an excluded head, for instance.
        /// </summary>
        public static List<(ushort Offset, ushort Length)> ClipToUnits(ushort offset, ushort length, ISet<int> neededUnits)
        {
            var runs = new List<(ushort Offset, ushort Length)>();

            if (length == 0 || neededUnits == null || neededUnits.Count == 0)
                return runs;

            int end = offset + length; // exclusive
            int runStart = -1;

            for (int unit = offset; unit < end; unit++)
            {
                if (neededUnits.Contains(unit))
                {
                    if (runStart < 0)
                        runStart = unit;
                }
                else if (runStart >= 0)
                {
                    runs.Add(((ushort)runStart, (ushort)(unit - runStart)));
                    runStart = -1;
                }
            }

            if (runStart >= 0)
                runs.Add(((ushort)runStart, (ushort)(end - runStart)));

            return runs;
        }
    }
}
