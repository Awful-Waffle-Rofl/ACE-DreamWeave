using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp.Templates;

namespace ACE.Server.Pvp
{
    /// <summary>Why /pvphue left an equipped item alone.</summary>
    public enum HueSkipReason
    {
        None,
        NoClothingBase,
        NoSubPalettes,
        UnsupportedPalette,
    }

    /// <summary>
    /// The DAT-free view of one equipped item that /pvphue reasons about. ValidPalettes is the key set of the item's
    /// ClothingBase ClothingSubPalEffects (null or empty when it has none).
    /// </summary>
    public sealed class HueItem
    {
        public uint Guid { get; }
        public string Name { get; }
        public uint? ClothingBase { get; }
        public IReadOnlyCollection<uint> ValidPalettes { get; }

        public HueItem(uint guid, string name, uint? clothingBase, IReadOnlyCollection<uint> validPalettes)
        {
            Guid = guid;
            Name = name;
            ClothingBase = clothingBase;
            ValidPalettes = validPalettes ?? new uint[0];
        }
    }

    /// <summary>The look an item had before its first /pvphue change.</summary>
    public readonly struct HueOriginal
    {
        public uint? Icon { get; }
        public int? PaletteTemplate { get; }
        public double? Shade { get; }

        // Look fields (/pvphue look). Absent (null) means the property did not exist and is REMOVED on restore.
        public uint? Setup { get; }
        public uint? ClothingBase { get; }
        public uint? PaletteBase { get; }
        public double? Shade2 { get; }
        public double? Shade3 { get; }
        public double? Shade4 { get; }
        public bool? IgnoreCloIcons { get; }

        public HueOriginal(uint? icon, int? paletteTemplate, double? shade,
            uint? setup = null, uint? clothingBase = null, uint? paletteBase = null,
            double? shade2 = null, double? shade3 = null, double? shade4 = null, bool? ignoreCloIcons = null)
        {
            IgnoreCloIcons = ignoreCloIcons;
            Icon = icon;
            PaletteTemplate = paletteTemplate;
            Shade = shade;
            Setup = setup;
            ClothingBase = clothingBase;
            PaletteBase = paletteBase;
            Shade2 = shade2;
            Shade3 = shade3;
            Shade4 = shade4;
        }
    }

    /// <summary>The look-bearing properties of a weenie or worn item. Null = the property is absent.</summary>
    public sealed class LookFields
    {
        public uint? Setup { get; set; }
        public uint? ClothingBase { get; set; }
        public uint? PaletteBase { get; set; }
        public uint? Icon { get; set; }
        public int? PaletteTemplate { get; set; }
        public double? Shade { get; set; }
        public double? Shade2 { get; set; }
        public double? Shade3 { get; set; }
        public double? Shade4 { get; set; }
        public bool? IgnoreCloIcons { get; set; }
    }

    /// <summary>
    /// What /pvphue look writes to an item. Target holds the new values (null = remove the property), except that
    /// KeepSetup / KeepIcon mean "leave the item's own value alone" (the source offered none to copy).
    /// </summary>
    public sealed class LookPlan
    {
        public LookFields Target { get; set; }
        public bool KeepSetup { get; set; }
        public bool KeepIcon { get; set; }
    }

    /// <summary>
    /// A world weenie read as a look source: the look fields, the slots it covers, and whether it carries raw palette / texture /
    /// anim rows. The ONE extraction behind /pvphue look and the team hue `@wcid` swap (see <see cref="PvpWorldLooks"/>), so the two
    /// cannot disagree about what a source's look is or whether it is valid.
    /// </summary>
    public sealed class PvpLookSource
    {
        public uint Wcid { get; set; }
        public string Name { get; set; }
        public LookFields Look { get; set; }

        /// <summary>The weenie's ValidLocations (Int 9), 0 when absent.</summary>
        public int ValidLocations { get; set; }

        public bool HasPaletteRows { get; set; }
        public bool HasTextureMapRows { get; set; }
        public bool HasAnimPartRows { get; set; }

        /// <summary>Why this weenie cannot be a look source (<see cref="PvpHueProbe.ValidateLookSource"/>), or null when it can.</summary>
        public string Problem() => PvpHueProbe.ValidateLookSource(Look, HasPaletteRows, HasTextureMapRows, HasAnimPartRows);
    }

    /// <summary>An equipped item as /pvphue look sees it: its CurrentWieldedLocation bits (null when not wielded).</summary>
    public sealed class HueWorn
    {
        public uint Guid { get; }
        public string Name { get; }
        public int? Location { get; }
        public bool HasOwnRows { get; }

        public HueWorn(uint guid, string name, int? location, bool hasOwnRows = false)
        {
            HasOwnRows = hasOwnRows;
            Guid = guid;
            Name = name;
            Location = location;
        }
    }

    public sealed class LookSelection
    {
        public List<uint> Chosen { get; } = new List<uint>();
        public List<KeyValuePair<uint, string>> Skipped { get; } = new List<KeyValuePair<uint, string>>();
    }

    public enum HueCommandKind
    {
        List,
        Reset,
        Apply,

        /// <summary>/pvphue audit: what every offered template's worn kit resolves to for each team hue.</summary>
        Audit,

        /// <summary>/pvphue suit &lt;templateKey&gt; &lt;red|blue|neutral|own&gt;: dress the selected player in a template's kit look.</summary>
        Suit,
    }

    /// <summary>A parsed /pvphue invocation. Shade is null when omitted (keep each item's own).</summary>
    public readonly struct HueCommand
    {
        public HueCommandKind Kind { get; }
        public int Palette { get; }
        public double? Shade { get; }

        /// <summary>/pvphue suit: the template key, lowercase.</summary>
        public string TemplateKey { get; }

        /// <summary>/pvphue suit: the hue to dress in, or null for "own" (the template's own look, no team hue).</summary>
        public KitHue? Hue { get; }

        /// <summary>
        /// /pvphue suit: true when the third argument was a numeric palette (Palette, and Shade when given) instead of a hue word. The
        /// preview then dresses the kit as one Tugak seat of that palette would be (<see cref="PvpTeamHue.FfaSeatDials"/>).
        /// </summary>
        public bool NumericPalette { get; }

        public HueCommand(HueCommandKind kind, int palette = 0, double? shade = null, string templateKey = null, KitHue? hue = null, bool numericPalette = false)
        {
            Kind = kind;
            Palette = palette;
            Shade = shade;
            TemplateKey = templateKey;
            Hue = hue;
            NumericPalette = numericPalette;
        }
    }

    /// <summary>
    /// Pure rules behind the /pvphue prototype probe (PvpHueCommands): which items accept a palette template, the
    /// palettes valid for every recolourable item, argument parsing, look-source validation and coverage selection,
    /// the look copy plan, and the restore decision. No DAT, Player or Session.
    /// </summary>
    public static class PvpHueProbe
    {
        /// <summary>
        /// Parses /pvphue arguments: "list", "reset", or "&lt;palette&gt; [shade]" with palette a non-negative integer and
        /// shade a finite number in [0,1] (invariant culture: "0,5" is rejected). "list", "reset" and "audit" take no further argument.
        /// "suit &lt;templateKey&gt; &lt;red|blue|neutral|own&gt;" takes exactly those two; "suit &lt;templateKey&gt; &lt;palette&gt; [shade]"
        /// previews one Tugak seat's palette (same palette and shade rules as "&lt;palette&gt; [shade]").
        /// </summary>
        public static bool TryParseArgs(string[] args, out HueCommand command)
        {
            command = default;

            if (args == null || args.Length == 0)
                return false;

            var first = args[0].ToLowerInvariant();

            if (first == "suit")
            {
                if (args.Length < 3 || args.Length > 4 || string.IsNullOrWhiteSpace(args[1]))
                    return false;

                var which = args[2].Trim().ToLowerInvariant();

                // "suit <key> <palette> [shade]": a numeric palette, previewed through the Tugak per-seat path.
                if (int.TryParse(which, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var suitPalette))
                {
                    if (suitPalette < 0)
                        return false;

                    double? suitShade = null;

                    if (args.Length == 4)
                    {
                        if (!double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ss)
                            || double.IsNaN(ss) || double.IsInfinity(ss) || ss < 0.0 || ss > 1.0)
                            return false;

                        suitShade = ss;
                    }

                    command = new HueCommand(HueCommandKind.Suit, suitPalette, suitShade, args[1].Trim().ToLowerInvariant(), numericPalette: true);
                    return true;
                }

                if (args.Length != 3)
                    return false;

                if (which == "own")
                {
                    command = new HueCommand(HueCommandKind.Suit, templateKey: args[1].Trim().ToLowerInvariant());
                    return true;
                }

                if (!PvpTeamHue.TryParseHue(which, out var hue))
                    return false;

                command = new HueCommand(HueCommandKind.Suit, templateKey: args[1].Trim().ToLowerInvariant(), hue: hue);
                return true;
            }

            if (args.Length > 2)
                return false;

            if (first == "audit")
            {
                if (args.Length != 1)
                    return false;

                command = new HueCommand(HueCommandKind.Audit);
                return true;
            }

            if (first == "list" || first == "reset")
            {
                if (args.Length != 1)
                    return false;

                command = new HueCommand(first == "list" ? HueCommandKind.List : HueCommandKind.Reset);
                return true;
            }

            if (!int.TryParse(args[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var palette) || palette < 0)
                return false;

            double? shade = null;

            if (args.Length == 2)
            {
                if (!double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)
                    || double.IsNaN(s) || double.IsInfinity(s) || s < 0.0 || s > 1.0)
                    return false;

                shade = s;
            }

            command = new HueCommand(HueCommandKind.Apply, palette, shade);
            return true;
        }

        /// <summary>EquipMask values (ACE.Entity EquipMask) of the PvP Arena armor slots, by /pvphue look slot name.</summary>
        private static readonly Dictionary<string, int> SlotMasks = new Dictionary<string, int>
        {
            ["helm"] = 0x1,          // HeadWear
            ["chest"] = 0x200,       // ChestArmor
            ["pauldrons"] = 0x800,   // UpperArmArmor
            ["bracers"] = 0x1000,    // LowerArmArmor
            ["gauntlets"] = 0x20,    // HandWear
            ["girth"] = 0x400,       // AbdomenArmor
            ["tassets"] = 0x2000,    // UpperLegArmor
            ["greaves"] = 0x4000,    // LowerLegArmor
            ["sollerets"] = 0x100,   // FootWear
        };

        /// <summary>
        /// Parses a comma-separated slot list. False (with the offending name) on an unknown or empty name.
        /// </summary>
        public static bool TryParseSlots(string text, out List<int> masks, out string badName)
        {
            masks = new List<int>();
            badName = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                badName = "";
                return false;
            }

            foreach (var raw in text.Split(','))
            {
                var name = raw.Trim().ToLowerInvariant();

                if (!SlotMasks.TryGetValue(name, out var mask))
                {
                    badName = raw.Trim();
                    return false;
                }

                if (!masks.Contains(mask))
                    masks.Add(mask);
            }

            return true;
        }

        /// <summary>The slot name for a wielded-location mask, or null when it is none of the nine armor slots.</summary>
        public static string SlotName(int location)
        {
            foreach (var kv in SlotMasks)
                if ((location & kv.Value) != 0)
                    return kv.Key;

            return null;
        }

        /// <summary>
        /// Why a weenie cannot be a look source, or null when it can: it needs a ClothingBase, and it must carry no raw
        /// palette / texture-map / anim-part rows (those disable the ClothingBase path).
        /// </summary>
        public static string ValidateLookSource(LookFields source, bool hasPaletteRows, bool hasTextureMapRows, bool hasAnimPartRows)
        {
            if (!source.ClothingBase.HasValue || source.ClothingBase.Value == 0)
                return "it has no ClothingBase";

            var rows = new List<string>();
            if (hasPaletteRows) rows.Add("palette");
            if (hasTextureMapRows) rows.Add("texture-map");
            if (hasAnimPartRows) rows.Add("anim-part");

            if (rows.Count > 0)
                return "it carries raw " + string.Join(", ", rows) + " rows (these disable the ClothingBase path)";

            return null;
        }

        /// <summary>
        /// Chooses the worn items a look lands on, by a SUBSET rule: an item is chosen only when its whole wielded
        /// location lies inside the allowed mask - the source's ValidLocations by default, or the OR of the explicit
        /// slot masks (which override the source's coverage). An item spanning slots outside it is skipped, as is one
        /// with its own palette/texture/anim rows (those disable the ClothingBase path) and one that is not wielded.
        /// </summary>
        public static LookSelection SelectLookTargets(IEnumerable<HueWorn> worn, int sourceValidLocations, IReadOnlyCollection<int> explicitMasks)
        {
            var result = new LookSelection();
            var allowed = sourceValidLocations;

            if (explicitMasks != null && explicitMasks.Count > 0)
            {
                allowed = 0;
                foreach (var m in explicitMasks)
                    allowed |= m;
            }

            foreach (var w in worn)
            {
                if (!w.Location.HasValue || w.Location.Value == 0)
                    result.Skipped.Add(new KeyValuePair<uint, string>(w.Guid, "not wielded"));
                else if (w.HasOwnRows)
                    result.Skipped.Add(new KeyValuePair<uint, string>(w.Guid, "has its own palette/texture/anim rows"));
                else if ((w.Location.Value & ~allowed) != 0)
                    result.Skipped.Add(new KeyValuePair<uint, string>(w.Guid, "spans slots outside the source's coverage"));
                else
                    result.Chosen.Add(w.Guid);
            }

            return result;
        }

        /// <summary>
        /// The look-field copy/remove plan: Setup, ClothingBase, PaletteBase, PaletteTemplate and Shade 1-4 come from the
        /// source (a field the source lacks is removed from the item; Setup is kept instead, since an item with no Setup
        /// breaks). IgnoreCloIcons is carried: true on the source means the source's own Icon is written (never the table
        /// icon) and the flag is set on the item; false or absent removes it. Otherwise Icon is the ClothingTable icon for
        /// the new palette when non-zero, else the source's own Icon, else kept.
        /// </summary>
        public static LookPlan PlanLook(LookFields source, uint tableIcon)
        {
            var plan = new LookPlan
            {
                Target = new LookFields
                {
                    Setup = source.Setup,
                    ClothingBase = source.ClothingBase,
                    PaletteBase = source.PaletteBase,
                    PaletteTemplate = source.PaletteTemplate,
                    Shade = source.Shade,
                    Shade2 = source.Shade2,
                    Shade3 = source.Shade3,
                    Shade4 = source.Shade4,
                    IgnoreCloIcons = source.IgnoreCloIcons == true ? true : (bool?)null,
                },
                KeepSetup = !source.Setup.HasValue,
            };

            if (source.IgnoreCloIcons == true)
            {
                if (source.Icon.HasValue)
                    plan.Target.Icon = source.Icon;
                else
                    plan.KeepIcon = true;
            }
            else if (tableIcon != 0)
                plan.Target.Icon = tableIcon;
            else if (source.Icon.HasValue)
                plan.Target.Icon = source.Icon;
            else
                plan.KeepIcon = true;

            return plan;
        }
        /// <summary>
        /// Restore decision for one remembered property: an original that was absent (null) means the property is
        /// REMOVED again, not written as zero.
        /// </summary>
        public static bool RestoreRemoves<T>(T? original) where T : struct => !original.HasValue;

        /// <summary>
        /// Whether to write the ClothingTable icon: only a non-zero icon, and never on an item that ignores clothing icons.
        /// </summary>
        public static bool ShouldWriteIcon(uint tableIcon, bool? ignoreCloIcons) =>
            tableIcon != 0 && ignoreCloIcons != true;

        /// <summary>
        /// Whether the item can take this palette. An id outside ClothingSubPalEffects must be refused: the appearance
        /// code silently falls back to the first entry for an unknown id, so applying it would look like a success.
        /// </summary>
        public static HueSkipReason Classify(HueItem item, int palette)
        {
            if (item.ClothingBase == null || item.ClothingBase.Value == 0)
                return HueSkipReason.NoClothingBase;

            if (item.ValidPalettes.Count == 0)
                return HueSkipReason.NoSubPalettes;

            if (palette < 0 || !item.ValidPalettes.Contains((uint)palette))
                return HueSkipReason.UnsupportedPalette;

            return HueSkipReason.None;
        }

        /// <summary>True when the item has a ClothingBase with at least one sub-palette (it can be recoloured at all).</summary>
        public static bool IsRecolourable(HueItem item) =>
            item.ClothingBase.HasValue && item.ClothingBase.Value != 0 && item.ValidPalettes.Count > 0;

        /// <summary>Palette ids valid for EVERY recolourable item, sorted. Empty when there is no recolourable item.</summary>
        public static List<uint> CommonPalettes(IEnumerable<HueItem> items)
        {
            HashSet<uint> common = null;

            foreach (var item in items.Where(IsRecolourable))
            {
                if (common == null)
                    common = new HashSet<uint>(item.ValidPalettes);
                else
                    common.IntersectWith(item.ValidPalettes);
            }

            return common == null ? new List<uint>() : common.OrderBy(p => p).ToList();
        }

        /// <summary>Items that cannot be recoloured at all (no ClothingBase, or no sub-palettes).</summary>
        public static List<HueItem> Unrecolourable(IEnumerable<HueItem> items) =>
            items.Where(i => !IsRecolourable(i)).ToList();
    }

    /// <summary>
    /// In-memory record of each item's look before its FIRST /pvphue change since the last reset, keyed by item guid.
    /// A second change must not overwrite it, or reset would restore a half-way look instead of the original.
    /// </summary>
    public sealed class HueMemory
    {
        private readonly Dictionary<uint, HueOriginal> originals = new Dictionary<uint, HueOriginal>();
        private readonly object sync = new object();

        /// <summary>Remembers the original; returns false (keeping the earlier record) if one already exists.</summary>
        public bool Remember(uint guid, HueOriginal original)
        {
            lock (sync)
            {
                if (originals.ContainsKey(guid))
                    return false;

                originals[guid] = original;
                return true;
            }
        }

        public bool TryGet(uint guid, out HueOriginal original)
        {
            lock (sync)
                return originals.TryGetValue(guid, out original);
        }

        public bool Forget(uint guid)
        {
            lock (sync)
                return originals.Remove(guid);
        }

        public int Count { get { lock (sync) return originals.Count; } }
    }
}
