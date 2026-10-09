using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.DatLoader
{
    /// <summary>
    /// Canonical item-properties-to-icon-key rule, shared by the pack exporter, the snapshot writer
    /// and the web app so they cannot drift. Mirrors WorldObject.CalculateObjDesc's ClothingBase
    /// branch. Pure given its `clothing` oracle.
    /// </summary>
    public static class IconKey
    {
        /// <summary>Key for an icon used exactly as the dat stores it: "0x060011F3".</summary>
        public static string Plain(uint id)
        {
            return "0x" + id.ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Per-PaletteTemplate variant key, e.g. "0x0600ABCD_p02". Carries the RESOLVED icon id and
        /// the template actually used, which is not always the one requested.
        /// </summary>
        public static string Variant(uint id, uint paletteTemplate)
        {
            return Plain(id) + "_p" + paletteTemplate.ToString("D2", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Resolves the three layer keys for one item. setupId, hasShade and ignoreCloIcons are the
        /// gates the client applies before overriding the icon; omitting any makes this rule
        /// disagree with what the player sees.
        /// </summary>
        public static IconKeySet Resolve(
            uint? iconId,
            uint? overlayId,
            uint? underlayId,
            int? paletteTemplate,
            uint? clothingBase,
            IClothingIconSource clothing,
            uint setupId,
            bool hasShade,
            bool ignoreCloIcons)
        {
            string overlayKey = overlayId.HasValue ? Plain(overlayId.Value) : null;
            string underlayKey = underlayId.HasValue ? Plain(underlayId.Value) : null;

            string baseKey = iconId.HasValue ? Plain(iconId.Value) : null;
            uint? resolvedIconId = iconId;
            uint? resolvedTemplate = null;

            if (clothingBase.HasValue && clothing != null)
            {
                ClothingIconTable table = clothing.TryGetTable(clothingBase.Value);

                if (table != null
                    && table.SetupIds.Contains(setupId)
                    && table.TemplateKeys.Count > 0
                    && (hasShade || paletteTemplate.HasValue))
                {
                    uint palOption = paletteTemplate.HasValue ? unchecked((uint)paletteTemplate.Value) : 0u;

                    // The client falls back to the FIRST entry in enumeration order, not the
                    // lowest-numbered one, so the key must name whichever template actually
                    // supplied the icon - that is the only one the pack has a file for.
                    uint effectiveTemplate = table.IconByTemplate.ContainsKey(palOption)
                        ? palOption
                        : table.TemplateKeys[0];

                    uint effectIcon;
                    if (table.IconByTemplate.TryGetValue(effectiveTemplate, out effectIcon)
                        && effectIcon > 0
                        && !ignoreCloIcons)
                    {
                        baseKey = Variant(effectIcon, effectiveTemplate);
                        resolvedIconId = effectIcon;
                        resolvedTemplate = effectiveTemplate;
                    }
                }
            }

            return new IconKeySet(baseKey, overlayKey, underlayKey, resolvedIconId, resolvedTemplate);
        }
    }

    /// <summary>The three layer keys for one item, plus what the base key resolved from.</summary>
    public class IconKeySet
    {
        public IconKeySet(string baseKey, string overlayKey, string underlayKey, uint? resolvedIconId, uint? resolvedPaletteTemplate)
        {
            BaseKey = baseKey;
            OverlayKey = overlayKey;
            UnderlayKey = underlayKey;
            ResolvedIconId = resolvedIconId;
            ResolvedPaletteTemplate = resolvedPaletteTemplate;
        }

        /// <summary>Bottom-most drawn layer's key, or null when the item declares no Icon.</summary>
        public string BaseKey { get; private set; }

        /// <summary>Top layer's key, or null. Never palette resolved.</summary>
        public string OverlayKey { get; private set; }

        /// <summary>Under layer's key, or null. Never palette resolved.</summary>
        public string UnderlayKey { get; private set; }

        /// <summary>The 0x06 id BaseKey names, after any ClothingBase override.</summary>
        public uint? ResolvedIconId { get; private set; }

        /// <summary>The PaletteTemplate that supplied BaseKey, or null when it is a plain key.</summary>
        public uint? ResolvedPaletteTemplate { get; private set; }
    }

    /// <summary>
    /// Read-only view of the one thing IconKey needs out of a ClothingTable. Injected rather than
    /// read from DatManager so the rule stays pure and testable.
    /// </summary>
    public interface IClothingIconSource
    {
        /// <summary>The table for this ClothingBase, or null when the dat has no such file.</summary>
        ClothingIconTable TryGetTable(uint clothingBase);
    }

    /// <summary>
    /// One ClothingTable, flattened to what icon resolution needs.
    /// </summary>
    public class ClothingIconTable
    {
        public ClothingIconTable(IReadOnlyList<uint> setupIds, IReadOnlyList<uint> templateKeys, IReadOnlyDictionary<uint, uint> iconByTemplate)
        {
            SetupIds = setupIds;
            TemplateKeys = templateKeys;
            IconByTemplate = iconByTemplate;
        }

        /// <summary>ClothingBaseEffects.Keys - the setups this table can dress.</summary>
        public IReadOnlyList<uint> SetupIds { get; private set; }

        /// <summary>
        /// ClothingSubPalEffects.Keys in DICTIONARY ENUMERATION ORDER. Must not be sorted: index 0
        /// is the client's fallback target.
        /// </summary>
        public IReadOnlyList<uint> TemplateKeys { get; private set; }

        /// <summary>PaletteTemplate to CloSubPalEffect.Icon. A value of 0 means "no icon here".</summary>
        public IReadOnlyDictionary<uint, uint> IconByTemplate { get; private set; }
    }
}
