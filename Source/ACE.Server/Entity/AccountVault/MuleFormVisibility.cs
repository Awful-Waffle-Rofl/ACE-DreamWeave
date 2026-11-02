using System;
using System.Collections.Generic;

using log4net;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Whether a creature body (Setup, plus the ClothingBase worn over it) draws anything at all.
    ///
    /// This is the server-side port of the criterion ACE.Content.Tools' creaturerender uses for its
    /// "nothing was drawn (every polygon was culled or fully transparent)" failure, minus the part that
    /// depends on the camera, plus the one thing the renderer ignores that the real client honours. A
    /// body DRAWS if it bakes a DefaultScript (particle effects are drawn even when every mesh is
    /// invisible), or if any part, after the ClothingBase has swapped in its own part models and
    /// texture ids, has a polygon that is
    ///   - a real polygon (3 or more points, every vertex present in the GfxObj), and
    ///   - on a surface that is not drawn fully translucent (Translucent flag with Translucency 1), and
    ///   - on a solid-colour surface (always opaque - the renderer forces it so), on a surface whose
    ///     texture cannot be resolved (the renderer paints placeholder grey), or on a texture with at
    ///     least one texel of non-zero alpha.
    ///
    /// WHY TRANSLUCENCY IS HERE. The Dark Sarcophagus (Setup 0x02000E1B) is one quad textured with an
    /// all-transparent DXT1 block plus sixteen tiny single-sided triangles on an opaque black texture
    /// whose Surface is Translucent with Translucency 1.0. The renderer reports it as drawing nothing
    /// only because it happens to cull those triangles from its one camera angle, and back-face culling
    /// is not ported (a model whose polygons face away from one yaw is not an invisible model). What
    /// makes the triangles invisible to the real client is the surface's full translucency, so that is
    /// what this checks.
    ///
    /// Three answers, because the caller must fail OPEN: true (draws nothing), false (draws), and null
    /// (could not tell - a dat read threw, or the Setup id is unusable). Null is never cached and
    /// never refuses an attunement. Every pixel format this does not decode counts as drawing.
    /// </summary>
    public static class MuleFormVisibility
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// true = the body draws nothing; false = it draws something; null = unknown (fail open).
        /// Never throws.
        /// </summary>
        public static bool? ReadDrawsNothing(uint setupId, uint clothingBaseId)
        {
            try
            {
                if (DatManager.PortalDat == null || !WorldObject.TryGetSetup(setupId, out var setup) || setup == null)
                    return null;

                if (TryDecideWithoutMesh(setup.Parts.Count, setup.DefaultScript, out var early))
                    return early;

                var partOverrides = new Dictionary<uint, uint>();
                var textureOverridesByPart = new Dictionary<uint, Dictionary<uint, uint>>();

                // The same substitution ObjDesc.Add does for a worn ClothingBase: it can swap a part's
                // whole GfxObj and specific texture ids.
                if (clothingBaseId != 0 && DatManager.PortalDat.AllFiles.ContainsKey(clothingBaseId))
                {
                    var clothing = DatManager.PortalDat.ReadFromDat<ClothingTable>(clothingBaseId);

                    if (clothing != null && clothing.ClothingBaseEffects.TryGetValue(setupId, out var baseEffect))
                    {
                        foreach (var objEffect in baseEffect.CloObjectEffects)
                        {
                            partOverrides[objEffect.Index] = objEffect.ModelId;

                            if (objEffect.CloTextureEffects.Count > 0)
                            {
                                var map = new Dictionary<uint, uint>();
                                foreach (var texEffect in objEffect.CloTextureEffects)
                                    map[texEffect.OldTexture] = texEffect.NewTexture;
                                textureOverridesByPart[objEffect.Index] = map;
                            }
                        }
                    }
                }

                var textureVisible = new Dictionary<uint, bool>();

                for (var partIndex = 0; partIndex < setup.Parts.Count; partIndex++)
                {
                    var gfxObjId = partOverrides.TryGetValue((uint)partIndex, out var overrideId) ? overrideId : setup.Parts[partIndex];

                    if (!DatManager.PortalDat.AllFiles.ContainsKey(gfxObjId))
                        continue;

                    var gfxObj = DatManager.PortalDat.ReadFromDat<GfxObj>(gfxObjId);

                    if (gfxObj == null || gfxObj.Polygons.Count == 0)
                        continue;

                    textureOverridesByPart.TryGetValue((uint)partIndex, out var textureOverrides);

                    foreach (var poly in gfxObj.Polygons.Values)
                    {
                        if (poly.NumPts < 3 || !HasAllVertices(gfxObj, poly))
                            continue;

                        if (PolygonDraws(gfxObj, poly, textureOverrides, textureVisible))
                            return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"[MULEFORM] ReadDrawsNothing: setup 0x{setupId:X8} clothingBase 0x{clothingBaseId:X8} unreadable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The verdicts that need no mesh. A baked script plays whatever the mesh looks like, so it is
        /// checked FIRST and means "draws". A Setup with no parts and no script is NOT a verdict:
        /// TryGetSetup hands back an empty fallback model for an invalid or missing id, so an empty
        /// Setup is unknown (null: not cached, fails open), never "draws nothing".
        /// </summary>
        internal static bool TryDecideWithoutMesh(int partCount, uint defaultScript, out bool? verdict)
        {
            if (defaultScript != 0)
            {
                verdict = false;
                return true;
            }

            if (partCount == 0)
            {
                verdict = null;
                return true;
            }

            verdict = null;
            return false;
        }

        private static bool HasAllVertices(GfxObj gfxObj, ACE.DatLoader.Entity.Polygon poly)
        {
            for (var k = 0; k < poly.NumPts; k++)
            {
                if (!gfxObj.VertexArray.Vertices.ContainsKey((ushort)poly.VertexIds[k]))
                    return false;
            }

            return true;
        }

        private static bool PolygonDraws(GfxObj gfxObj, ACE.DatLoader.Entity.Polygon poly,
                                         Dictionary<uint, uint> textureOverrides, Dictionary<uint, bool> textureVisible)
        {
            // PosSurface is an INDEX into this GfxObj's own Surfaces list. One that does not resolve is
            // painted placeholder grey by the renderer, i.e. it draws.
            if (poly.PosSurface < 0 || poly.PosSurface >= gfxObj.Surfaces.Count)
                return true;

            var surfaceId = gfxObj.Surfaces[poly.PosSurface];

            if (!DatManager.PortalDat.AllFiles.ContainsKey(surfaceId))
                return true;

            var surface = DatManager.PortalDat.ReadFromDat<Surface>(surfaceId);

            if (surface == null)
                return true;

            if (surface.Type.HasFlag(SurfaceType.Translucent) && surface.Translucency >= 1f)
                return false;

            // A solid-colour surface is forced opaque by the renderer.
            if (surface.OrigTextureId == 0)
                return true;

            // Texel alpha only means something on a clip-mapped or alpha-blended surface. On a plain
            // image surface the client may ignore it, so such a polygon counts as drawing.
            if ((surface.Type & (SurfaceType.Base1ClipMap | SurfaceType.Alpha | SurfaceType.InvAlpha)) == 0)
                return true;

            var textureId = surface.OrigTextureId;

            if (textureOverrides != null && textureOverrides.TryGetValue(textureId, out var swapped))
                textureId = swapped;

            if (!textureVisible.TryGetValue(textureId, out var visible))
            {
                visible = TextureHasVisibleTexel(textureId);
                textureVisible[textureId] = visible;
            }

            return visible;
        }

        /// <summary>
        /// Whether the SurfaceTexture's first Texture has at least one texel with non-zero alpha. An
        /// unresolvable or undecodable texture counts as VISIBLE (the renderer paints it grey), and so
        /// does any pixel format this does not decode: a wrong "visible" only lets an attunement
        /// through, a wrong "invisible" would refuse a good body.
        /// </summary>
        private static bool TextureHasVisibleTexel(uint surfaceTextureId)
        {
            if (!DatManager.PortalDat.AllFiles.ContainsKey(surfaceTextureId))
                return true;

            var surfaceTexture = DatManager.PortalDat.ReadFromDat<SurfaceTexture>(surfaceTextureId);

            if (surfaceTexture == null || surfaceTexture.Textures.Count == 0)
                return true;

            var textureId = surfaceTexture.Textures[0];

            Texture texture = null;

            if (DatManager.PortalDat.AllFiles.ContainsKey(textureId))
                texture = DatManager.PortalDat.ReadFromDat<Texture>(textureId);

            // Some textures' pixel payload only exists in client_highres.dat; the portal entry comes
            // back with a real header and no data.
            if ((texture == null || texture.SourceData == null) && DatManager.HighResDat != null
                && DatManager.HighResDat.AllFiles.ContainsKey(textureId))
                texture = DatManager.HighResDat.ReadFromDat<Texture>(textureId);

            if (texture == null || texture.SourceData == null || texture.Width <= 0 || texture.Height <= 0)
                return true;

            var data = texture.SourceData;
            var texels = texture.Width * texture.Height;

            switch (texture.Format)
            {
                case SurfacePixelFormat.PFID_INDEX16:
                case SurfacePixelFormat.PFID_P8:
                {
                    if (texture.DefaultPaletteId == null)
                        return true;

                    var palette = DatManager.PortalDat.ReadFromDat<Palette>(texture.DefaultPaletteId.Value);

                    if (palette == null)
                        return true;

                    return IndexedHasVisibleTexel(data, texels, texture.Format == SurfacePixelFormat.PFID_INDEX16, palette.Colors);
                }

                case SurfacePixelFormat.PFID_A8R8G8B8:
                    return Argb8HasVisibleTexel(data, texels);

                case SurfacePixelFormat.PFID_DXT1:
                    return Dxt1HasVisibleTexel(data, texture.Width, texture.Height);

                default:
                    return true;
            }
        }

        /// <summary>
        /// Palette-indexed texels (16-bit INDEX16 when <paramref name="wide"/>, else 8-bit P8): visible
        /// if any texel's palette entry has non-zero alpha. An index outside the palette is transparent
        /// black, as in the renderer. A buffer too short for the texel count is unknown, so visible.
        /// </summary>
        internal static bool IndexedHasVisibleTexel(byte[] data, int texels, bool wide, IList<uint> paletteColors)
        {
            if (data.Length < texels * (wide ? 2 : 1))
                return true;

            for (var i = 0; i < texels; i++)
            {
                var idx = wide ? BitConverter.ToInt16(data, i * 2) : data[i];

                if (idx >= 0 && idx < paletteColors.Count && (paletteColors[idx] >> 24) != 0)
                    return true;
            }

            return false;
        }

        /// <summary>A8R8G8B8 texels (little-endian BGRA bytes): visible if any alpha byte is non-zero.</summary>
        internal static bool Argb8HasVisibleTexel(byte[] data, int texels)
        {
            if (data.Length < texels * 4)
                return true;

            for (var i = 0; i < texels; i++)
            {
                if (data[i * 4 + 3] != 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// DXT1 carries alpha only as punch-through: a block whose first colour is not greater than its
        /// second is in 3-colour mode, and index 3 there is transparent. A block with c0 greater than c1
        /// is fully opaque. Texels past the image edge in a partial block are padding and do not count.
        /// </summary>
        internal static bool Dxt1HasVisibleTexel(byte[] data, int width, int height)
        {
            var blocksX = (width + 3) / 4;
            var blocksY = (height + 3) / 4;

            if (data.Length < blocksX * blocksY * 8)
                return true;

            for (var by = 0; by < blocksY; by++)
            {
                for (var bx = 0; bx < blocksX; bx++)
                {
                    var offset = (by * blocksX + bx) * 8;
                    var c0 = BitConverter.ToUInt16(data, offset);
                    var c1 = BitConverter.ToUInt16(data, offset + 2);
                    var lookup = BitConverter.ToUInt32(data, offset + 4);

                    for (var ty = 0; ty < 4; ty++)
                    {
                        for (var tx = 0; tx < 4; tx++)
                        {
                            if (bx * 4 + tx >= width || by * 4 + ty >= height)
                                continue;

                            var index = (lookup >> (2 * (4 * ty + tx))) & 0x3;

                            if (c0 > c1 || index != 3)
                                return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
