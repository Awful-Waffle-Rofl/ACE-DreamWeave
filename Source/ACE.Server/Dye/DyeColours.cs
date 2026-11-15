using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Dye
{
    /// <summary>One named dye colour: a target RGB that DyeMatcher turns into the nearest existing client palette.</summary>
    public sealed class DyeColour
    {
        public string Key { get; }
        public string Name { get; }
        public string Group { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public DyeColour(string group, string name, uint rgb)
        {
            Group = group;
            Name = name;
            Key = DyeColours.Normalise(name);
            R = (byte)(rgb >> 16);
            G = (byte)(rgb >> 8);
            B = (byte)rgb;
        }

        public string Hex => $"{R:X2}{G:X2}{B:X2}";
    }

    /// <summary>
    /// The curated custom dye list. Colours are targets, not palette ids: the client has no palette that is the same
    /// colour on every item (most clothing palettes change hue from one range to the next), so DyeMatcher picks, per
    /// item range, the existing palette closest to the target there while keeping the item's own shading.
    /// Trimmed from 86 to 65 by measuring each colour on five armour pieces: dropped the pastels the
    /// stock palettes cannot reach (they all land on the same pale grey) and near twins (Ruby, Saffron, Sea Green, ...).
    /// Order matters only for listing; keys must stay stable once players have dyed items (a removed key renders
    /// as the item's own colour).
    /// </summary>
    public static class DyeColours
    {
        public static readonly IReadOnlyList<DyeColour> All = new List<DyeColour>
        {
            new("Reds", "Crimson", 0xA01828), new("Reds", "Scarlet", 0xC8241C), new("Reds", "Blood Red", 0x7A0A0A),
            new("Reds", "Wine", 0x5E1424), new("Reds", "Maroon", 0x4A1010),
            new("Reds", "Brick", 0x8E3A2A), new("Reds", "Rose", 0xB85068),

            new("Pinks", "Hot Pink", 0xE0408A),
            new("Pinks", "Fuchsia", 0xC02890), new("Pinks", "Magenta", 0xA0207A), new("Pinks", "Salmon", 0xE08870),
            new("Pinks", "Coral", 0xE86850),

            new("Oranges", "Orange", 0xE07018), new("Oranges", "Burnt Orange", 0xB04A10),
            new("Oranges", "Amber", 0xD89010), new("Oranges", "Rust", 0x8A3A14), new("Oranges", "Copper", 0xA85A30),
            new("Oranges", "Peach", 0xF0B088),

            new("Yellows", "Gold", 0xC8A020), new("Yellows", "Yellow", 0xE0D020), new("Yellows", "Lemon", 0xF0E860),
            new("Yellows", "Mustard", 0xA88A18), new("Yellows", "Honey", 0xC89040),

            new("Browns", "Tan", 0xB89870), new("Browns", "Khaki", 0xA89A68),
            new("Browns", "Camel", 0xA07840), new("Browns", "Brown", 0x6A4424), new("Browns", "Chocolate", 0x4A2C18),
            new("Browns", "Chestnut", 0x7A3A20), new("Browns", "Umber", 0x503820), new("Browns", "Bronze", 0x8A6A30),

            new("Greens", "Olive", 0x6A6A20), new("Greens", "Moss", 0x5A6A30), new("Greens", "Forest Green", 0x1E4A20),
            new("Greens", "Hunter Green", 0x24402C), new("Greens", "Emerald", 0x108A48), new("Greens", "Jade", 0x309070),
            new("Greens", "Green", 0x309030), new("Greens", "Lime", 0x8AC820), new("Greens", "Chartreuse", 0xA8D030),
            new("Greens", "Pine", 0x1A3A30),

            new("Teals", "Teal", 0x107878), new("Teals", "Turquoise", 0x30B0A8),
            new("Teals", "Cyan", 0x20A8C8), new("Teals", "Dark Teal", 0x0C4448),

            new("Blues", "Azure", 0x3088D8), new("Blues", "Cornflower", 0x6890D8),
            new("Blues", "Royal Blue", 0x2848B8), new("Blues", "Sapphire", 0x103080),
            new("Blues", "Navy", 0x101C50), new("Blues", "Midnight Blue", 0x0C1030), new("Blues", "Steel Blue", 0x4A6A90),
            new("Blues", "Slate", 0x5A6878),

            new("Purples", "Indigo", 0x301860), new("Purples", "Violet", 0x6A30B0), new("Purples", "Purple", 0x602080),
            new("Purples", "Royal Purple", 0x4A1870), new("Purples", "Plum", 0x5A2050),
            new("Purples", "Orchid", 0xB060B0),

            new("Neutrals", "Black", 0x101010), new("Neutrals", "Charcoal", 0x2C2C2C), new("Neutrals", "Iron Grey", 0x4A4A4A),
            new("Neutrals", "Grey", 0x7A7A7A), new("Neutrals", "Ash", 0x9A9890),
            new("Neutrals", "White", 0xF0F0F0),

            // Bought with Sky Seals (a Sky Dye Charter, FraysSky.DyeGroup). Not yet measured on armour.
            new("Sky", "Sky Blue", 0x4A90D0), new("Sky", "Storm", 0x3C4858),
            new("Sky", "Dawn", 0xC86070), new("Sky", "Twilight", 0x3A2868),
        };

        private static readonly Dictionary<string, DyeColour> byKey = All.ToDictionary(c => c.Key);
        private static readonly Dictionary<string, int> indexOf = All.Select((c, i) => (c.Key, i)).ToDictionary(t => t.Key, t => t.i);

        public static IEnumerable<string> Groups => All.Select(c => c.Group).Distinct();

        /// <summary>"Forest Green", "forest-green" and "forestgreen" are the same key.</summary>
        public static string Normalise(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        public static DyeColour Find(string nameOrKey) =>
            byKey.TryGetValue(Normalise(nameOrKey), out var c) ? c : null;

        public static int IndexOf(DyeColour c) => indexOf.TryGetValue(c.Key, out var i) ? i : -1;

        public static IEnumerable<DyeColour> InGroup(string group) =>
            All.Where(c => string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Group.TrimEnd('s'), group, StringComparison.OrdinalIgnoreCase));
    }
}
