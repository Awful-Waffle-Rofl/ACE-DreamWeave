using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The palette ids and icons a ClothingTable (a ClothingBase id) lists, read once from the client portal dat and cached. The one
    /// DAT-facing piece of team hue (Docs/Pvp/TEMPLATES.md "Team hue"): the live player gateway and the /pvphue audit and preview
    /// both read through it, so they see the same lists. An unreadable table is cached as "no palettes" and never throws.
    /// </summary>
    internal static class PvpClothingPalettes
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpClothingPalettes));

        private sealed class Info
        {
            public uint[] Palettes;
            public Dictionary<uint, uint> Icons;
        }

        private static readonly ConcurrentDictionary<uint, Info> cache = new ConcurrentDictionary<uint, Info>();

        /// <summary>
        /// Reads one ClothingTable's palette id to icon map, or null when it cannot be read. Test seam: production reads the portal dat.
        /// A throw is treated like null.
        /// </summary>
        internal static Func<uint, IReadOnlyDictionary<uint, uint>> TableReader = id =>
        {
            var table = DatManager.PortalDat?.ReadFromDat<ClothingTable>(id);

            return table?.ClothingSubPalEffects.ToDictionary(kv => kv.Key, kv => kv.Value.Icon);
        };

        /// <summary>Forgets every cached table. Test use.</summary>
        internal static void ClearCache() => cache.Clear();

        private static readonly Info Unreadable = new Info();

        /// <summary>
        /// The cached table, reading it on first use. Only a SUCCESSFUL read is cached: a failed one (dat not loaded yet, table missing,
        /// a throw) is returned uncached and retried on the next call, so one early failure cannot leave every later match undressed.
        /// </summary>
        private static Info Read(uint clothingBase)
        {
            if (cache.TryGetValue(clothingBase, out var cached))
                return cached;

            try
            {
                var icons = TableReader(clothingBase);

                if (icons == null)
                    return Unreadable;

                var info = new Info { Palettes = icons.Keys.ToArray(), Icons = new Dictionary<uint, uint>(icons) };
                cache[clothingBase] = info;
                return info;
            }
            catch (Exception ex)
            {
                log.Warn($"[PVPTEMPLATE] team hue: ClothingTable 0x{clothingBase:X8} could not be read (will retry): {ex.Message}");
                return Unreadable;
            }
        }

        /// <summary>The palette ids the table lists, or null when it could not be read.</summary>
        public static IReadOnlyCollection<uint> Palettes(uint clothingBase) => Read(clothingBase).Palettes;

        /// <summary>The icon the table carries for <paramref name="palette"/>, or 0 for none.</summary>
        public static uint Icon(uint clothingBase, uint palette)
        {
            var icons = Read(clothingBase).Icons;

            return icons != null && icons.TryGetValue(palette, out var icon) ? icon : 0;
        }
    }
}