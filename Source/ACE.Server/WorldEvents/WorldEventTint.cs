using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Models;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The corrupted tint (protect.corrupted.tintPalette): one client palette laid over a creature's whole palette.
    ///
    /// How it shows. A creature's look is sent as an ObjDesc: a base palette plus sub-palettes, each covering a range of
    /// the 2048 palette colors. A sub-palette sent with offset 0 and length 0 covers all of them (the client reads a
    /// length of 0 as 256 x 8 colors, as ACE.DatLoader's SubPalette does), so every palette-indexed texture on the body
    /// draws its colors from the tint palette. Added last, it wins over the creature's own colors. The chosen palettes
    /// are ramps of one hue from dark to bright, so the body keeps its shading in that hue.
    ///
    /// Where it cannot show: a texture stored in true color (no palette) and a solid-color surface keep their look. A
    /// model with no palette-indexed texture at all is reported at spawn (<see cref="CanShowOn"/>); the corrupted aura
    /// still marks it.
    /// </summary>
    public static class WorldEventTint
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventTint));

        /// <summary>The client palette range (DAT file type 0x04).</summary>
        public const uint PaletteMin = 0x04000000, PaletteMax = 0x0400FFFF;

        /// <summary>
        /// goals.json's tintPalette (D6 - pure): "0x04001381", "67113857", or "none" / "" / "0" for no tint (0). False for
        /// anything that is not a palette id.
        /// </summary>
        public static bool TryParsePalette(string text, out uint palette)
        {
            palette = 0;

            if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || text.Trim() == "0")
                return true;

            var t = text.Trim();
            uint value;

            var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);

            if (!ok || value < PaletteMin || value > PaletteMax)
                return false;

            palette = value;
            return true;
        }

        /// <summary>
        /// Lays <paramref name="palette"/> over the whole of <paramref name="objDesc"/>'s palette (D6 - pure): one
        /// sub-palette, offset 0 and length 0 (all 2048 colors), added last. The ObjDesc carries a base palette whenever
        /// it has sub-palettes; one with none gets the tint as its base too. Nothing for palette 0.
        /// </summary>
        public static void Apply(ACE.Entity.ObjDesc objDesc, uint palette)
        {
            if (objDesc == null || palette == 0)
                return;

            objDesc.SubPalettes.Add(new PropertiesPalette { SubPaletteId = palette, Offset = 0, Length = 0 });

            if (objDesc.PaletteID == 0)
                objDesc.PaletteID = palette;
        }

        private static readonly ConcurrentDictionary<uint, bool> canShowBySetup = new ConcurrentDictionary<uint, bool>();

        /// <summary>
        /// Whether a tint can show on a model (live: reads the client DAT once per setup): true when at least one of its
        /// parts has a surface whose texture is palette-indexed (P8 or INDEX16). Unknown (DAT not readable) counts as true.
        /// </summary>
        public static bool CanShowOn(uint setupId)
        {
            if (setupId == 0)
                return true;

            return canShowBySetup.GetOrAdd(setupId, id =>
            {
                try
                {
                    var setup = DatManager.PortalDat?.ReadFromDat<SetupModel>(id);

                    if (setup == null)
                        return true;

                    foreach (var part in setup.Parts)
                    {
                        var gfx = DatManager.PortalDat.ReadFromDat<GfxObj>(part);

                        foreach (var surfaceId in gfx?.Surfaces ?? new List<uint>())
                        {
                            var surface = DatManager.PortalDat.ReadFromDat<Surface>(surfaceId);

                            if (surface == null || surface.OrigTextureId == 0 || (surface.Type & SurfaceType.Base1Solid) != 0)
                                continue;

                            var surfaceTexture = DatManager.PortalDat.ReadFromDat<SurfaceTexture>(surface.OrigTextureId);

                            foreach (var textureId in surfaceTexture?.Textures ?? new List<uint>())
                            {
                                var texture = DatManager.PortalDat.ReadFromDat<Texture>(textureId);

                                if (IsPaletted(texture?.Format))
                                    return true;
                            }
                        }
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    log.Debug($"[WORLDEVENT] tint check for setup 0x{id:X8} failed", ex);
                    return true;
                }
            });
        }

        /// <summary>Whether a texture format draws its colors from a palette (D6 - pure).</summary>
        public static bool IsPaletted(SurfacePixelFormat? format) =>
            format == SurfacePixelFormat.PFID_P8 || format == SurfacePixelFormat.PFID_INDEX16;
    }
}
