using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using log4net;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Models;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Computes, from client_portal.dat (and client_highres.dat when present), the set of /8 palette
    /// units that a worn item's VISIBLE textures actually index on a given wearer setup. Used only for
    /// items carrying a non-empty PropertyString.ClothingPartExclusions set, so Creature.CalculateObjDesc
    /// can clip the item's sub-palette ranges to what its remaining parts need (see
    /// ClothingPartExclusions.ClipToUnits).
    ///
    /// "Visible" means: every non-excluded CloObjectEffect of the item's ClothingTable on that setup,
    /// starting from the effect's GfxObj surfaces (Surface.OrigTextureId), with the effect's own
    /// CloTextureEffects applied and then the item's own texture_map rows chained on the same way
    /// ObjDesc.MergeItemTextureChange chains them. Where two swaps could apply to one surface, both
    /// results are counted - over-keeping a unit only means a range is clipped less, never wrongly.
    /// A part the item's PropertyString.WornModelOverrides replaces is measured on the override model
    /// instead, and override-only parts are included.
    ///
    /// Both levels are cached: texture data in the dats is immutable, so per-texture unit sets live
    /// for the process lifetime, and the per-item union is keyed on everything that feeds it. A null
    /// result means "could not be computed" and the caller must fall back to emitting the item's
    /// palettes unclipped (fail open) - palettes are never dropped because of a read error.
    /// </summary>
    public static class ClothingVisiblePaletteUnits
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// SurfaceTexture (0x05) id -> sorted /8 units its palette-indexed pixels use. A null value
        /// means the texture could not be read.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, int[]> textureUnits = new ConcurrentDictionary<uint, int[]>();

        /// <summary>
        /// Per-item key -> union of units. A null value means the union could not be computed
        /// (fail open), cached so the failure is computed and logged once per key.
        ///
        /// Uncapped by design, and that is safe only because of a precondition: the key includes the
        /// item's texture_map rows, and for exclusion items those rows are weenie-authored and never
        /// mutated at runtime, so the key space is bounded by the number of exclusion weenies times
        /// wearer setups. If a dyeable or tinkerable exclusion item is ever added (per-instance rows),
        /// this cache needs a cap or eviction.
        /// </summary>
        private static readonly ConcurrentDictionary<string, HashSet<int>> itemUnits = new ConcurrentDictionary<string, HashSet<int>>();

        /// <summary>
        /// Returns the /8 units the item's visible textures index, or null when any visible texture
        /// could not be read or decoded (the caller then leaves the item's palettes unclipped).
        /// The returned set is shared and must not be modified.
        /// </summary>
        /// <param name="modelOverrides">The item's PropertyString.WornModelOverrides, or null. An overridden
        /// part is measured on its override model (with no ClothingTable texture effects, since
        /// CalculateObjDesc skips the table's entry for that part), and override parts the table does
        /// not dress at all are measured too.</param>
        public static HashSet<int> Get(uint clothingBase, uint setupId, ClothingBaseEffect effect, ICollection<byte> exclusions, IList<PropertiesTextureMap> itemTextures, IReadOnlyDictionary<byte, uint> modelOverrides = null)
        {
            var key = BuildKey(clothingBase, setupId, exclusions, itemTextures, modelOverrides);

            return itemUnits.GetOrAdd(key, _ => Compute(clothingBase, setupId, effect, exclusions, itemTextures, modelOverrides));
        }

        private static string BuildKey(uint clothingBase, uint setupId, ICollection<byte> exclusions, IList<PropertiesTextureMap> itemTextures, IReadOnlyDictionary<byte, uint> modelOverrides)
        {
            var sb = new StringBuilder();
            sb.Append(clothingBase.ToString("X8")).Append('|').Append(setupId.ToString("X8")).Append('|');

            foreach (var part in exclusions.OrderBy(p => p))
                sb.Append(part).Append(',');

            sb.Append('|');

            if (itemTextures != null)
            {
                // Row order matters to the chaining, so it is kept as given.
                foreach (var row in itemTextures)
                    sb.Append(row.PartIndex).Append(':').Append(row.OldTexture.ToString("X8")).Append('>').Append(row.NewTexture.ToString("X8")).Append(';');
            }

            sb.Append('|');

            if (modelOverrides != null)
            {
                foreach (var pair in modelOverrides.OrderBy(p => p.Key))
                    sb.Append(pair.Key).Append('=').Append(pair.Value.ToString("X8")).Append(';');
            }

            return sb.ToString();
        }

        private static HashSet<int> Compute(uint clothingBase, uint setupId, ClothingBaseEffect effect, ICollection<byte> exclusions, IList<PropertiesTextureMap> itemTextures, IReadOnlyDictionary<byte, uint> modelOverrides)
        {
            try
            {
                var units = new HashSet<int>();
                var coveredParts = new HashSet<byte>();

                // The parts this item shows, with the model each one shows: the ClothingTable's own
                // effect, or the item's WornModelOverrides model where one replaces it (the table's
                // texture effects for that part are then dropped, as CalculateObjDesc drops them).
                var visibleParts = new List<(byte Part, uint ModelId, IEnumerable<CloTextureEffect> TextureEffects)>();

                foreach (var objectEffect in effect.CloObjectEffects)
                {
                    var effectPart = (byte)objectEffect.Index;

                    if (exclusions.Contains(effectPart))
                        continue;

                    if (modelOverrides != null && modelOverrides.TryGetValue(effectPart, out var overrideModel))
                        visibleParts.Add((effectPart, overrideModel, Enumerable.Empty<CloTextureEffect>()));
                    else
                        visibleParts.Add((effectPart, objectEffect.ModelId, objectEffect.CloTextureEffects));
                }

                if (modelOverrides != null)
                {
                    foreach (var pair in modelOverrides)
                    {
                        if (exclusions.Contains(pair.Key) || visibleParts.Any(v => v.Part == pair.Key))
                            continue;

                        visibleParts.Add((pair.Key, pair.Value, Enumerable.Empty<CloTextureEffect>()));
                    }
                }

                foreach (var visiblePart in visibleParts)
                {
                    var partNum = visiblePart.Part;

                    coveredParts.Add(partNum);

                    // This part's swaps, in the order CalculateObjDesc produces them: the ClothingTable's
                    // own entries, then the item's rows chained onto them.
                    var swaps = new List<(uint Old, uint New)>();

                    foreach (var textureEffect in visiblePart.TextureEffects)
                    {
                        if (!swaps.Contains((textureEffect.OldTexture, textureEffect.NewTexture)))
                            swaps.Add((textureEffect.OldTexture, textureEffect.NewTexture));
                    }

                    if (itemTextures != null)
                    {
                        foreach (var row in itemTextures)
                        {
                            if (row.PartIndex != partNum)
                                continue;

                            var chained = false;

                            for (var i = 0; i < swaps.Count; i++)
                            {
                                if (swaps[i].New != row.OldTexture)
                                    continue;

                                swaps[i] = (swaps[i].Old, row.NewTexture);
                                chained = true;
                            }

                            if (!chained && !swaps.Contains((row.OldTexture, row.NewTexture)))
                                swaps.Add((row.OldTexture, row.NewTexture));
                        }
                    }

                    // ReadFromDat returns an empty object for an id the dat does not hold rather than
                    // throwing, which here would read as "no textures" and UNDER-clip the item. Check
                    // existence explicitly and fail open on a miss.
                    if (!DatManager.PortalDat.AllFiles.ContainsKey(visiblePart.ModelId))
                        return Fail(clothingBase, setupId, $"GfxObj 0x{visiblePart.ModelId:X8} on part {partNum} is not in client_portal.dat");

                    var gfxObj = DatManager.PortalDat.ReadFromDat<GfxObj>(visiblePart.ModelId);

                    // None of the 15318 GfxObjs in the current client_portal.dat has zero surfaces, so an
                    // empty list means a bad or wrongly typed id, not a legitimately untextured model.
                    if (gfxObj.Surfaces == null || gfxObj.Surfaces.Count == 0)
                        return Fail(clothingBase, setupId, $"GfxObj 0x{visiblePart.ModelId:X8} on part {partNum} has no surfaces");

                    foreach (var surfaceId in gfxObj.Surfaces.Distinct())
                    {
                        if (!DatManager.PortalDat.AllFiles.ContainsKey(surfaceId))
                            return Fail(clothingBase, setupId, $"Surface 0x{surfaceId:X8} of GfxObj 0x{visiblePart.ModelId:X8} is not in client_portal.dat");

                        var surface = DatManager.PortalDat.ReadFromDat<Surface>(surfaceId);

                        // A solid-color surface carries no texture and indexes no palette.
                        if (surface.OrigTextureId == 0)
                            continue;

                        var shown = swaps.Where(s => s.Old == surface.OrigTextureId).Select(s => s.New).ToList();
                        if (shown.Count == 0)
                            shown.Add(surface.OrigTextureId);

                        foreach (var textureId in shown)
                        {
                            var textureSet = GetTextureUnits(textureId);
                            if (textureSet == null)
                                return Fail(clothingBase, setupId, $"texture 0x{textureId:X8} on part {partNum} could not be read");

                            units.UnionWith(textureSet);
                        }
                    }
                }

                // Item rows on parts this ClothingTable does not dress are still merged into the
                // wearer by CalculateObjDesc and may land on another piece's surface there. Count their
                // targets too - over-keeping is the safe direction.
                if (itemTextures != null)
                {
                    foreach (var row in itemTextures)
                    {
                        if (coveredParts.Contains(row.PartIndex) || exclusions.Contains(row.PartIndex))
                            continue;

                        var textureSet = GetTextureUnits(row.NewTexture);
                        if (textureSet == null)
                            return Fail(clothingBase, setupId, $"texture 0x{row.NewTexture:X8} (item row, part {row.PartIndex}) could not be read");

                        units.UnionWith(textureSet);
                    }
                }

                return units;
            }
            catch (Exception ex)
            {
                return Fail(clothingBase, setupId, ex.Message);
            }
        }

        private static HashSet<int> Fail(uint clothingBase, uint setupId, string reason)
        {
            log.Debug($"ClothingVisiblePaletteUnits: ClothingBase 0x{clothingBase:X8} on setup 0x{setupId:X8}: {reason}; leaving this item's palettes unclipped.");
            return null;
        }

        /// <summary>
        /// /8 units used by a SurfaceTexture's palette-indexed images, unioned across every image it
        /// lists and across the portal and high-res dats. Non-palette formats contribute nothing.
        /// Returns null if a listed image exists in neither dat or cannot be unpacked.
        /// </summary>
        private static int[] GetTextureUnits(uint surfaceTextureId)
        {
            return textureUnits.GetOrAdd(surfaceTextureId, id =>
            {
                try
                {
                    if (!DatManager.PortalDat.AllFiles.ContainsKey(id))
                        return null;

                    var surfaceTexture = DatManager.PortalDat.ReadFromDat<SurfaceTexture>(id);
                    var units = new HashSet<int>();

                    foreach (var imageId in surfaceTexture.Textures)
                    {
                        var found = false;

                        if (TryAddImageUnits(DatManager.PortalDat, imageId, units))
                            found = true;

                        if (DatManager.HighResDat != null && TryAddImageUnits(DatManager.HighResDat, imageId, units))
                            found = true;

                        if (!found)
                            return null;
                    }

                    return units.OrderBy(u => u).ToArray();
                }
                catch (Exception ex)
                {
                    log.Debug($"ClothingVisiblePaletteUnits: SurfaceTexture 0x{id:X8}: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Unpacks one image directly from the dat, deliberately bypassing ReadFromDat so the pixel
        /// data is not pinned in the dat FileCache for the process lifetime - only the small unit set
        /// is kept.
        /// </summary>
        private static bool TryAddImageUnits(DatDatabase dat, uint imageId, HashSet<int> units)
        {
            if (!dat.AllFiles.ContainsKey(imageId))
                return false;

            var datReader = dat.GetReaderForFile(imageId);
            if (datReader == null)
                return false;

            var texture = new Texture();

            using (var memoryStream = new MemoryStream(datReader.Buffer))
            using (var reader = new BinaryReader(memoryStream))
                texture.Unpack(reader);

            var data = texture.SourceData;

            if (data == null)
                return true;

            switch (texture.Format)
            {
                case SurfacePixelFormat.PFID_INDEX16:
                    for (var i = 0; i + 1 < data.Length; i += 2)
                        units.Add(BitConverter.ToUInt16(data, i) / 8);
                    break;

                case SurfacePixelFormat.PFID_P8:
                    foreach (var b in data)
                        units.Add(b / 8);
                    break;

                    // Every other format carries its own colors and indexes no palette.
            }

            return true;
        }
    }
}
