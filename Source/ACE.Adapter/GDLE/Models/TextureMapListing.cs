using System.Text.Json.Serialization;

namespace ACE.Adapter.GDLE.Models
{
    /// <summary>
    /// A single per-part texture override (WeeniePropertiesTextureMap) - swaps the SurfaceTexture (0x05) id used
    /// by one Setup part index for another, independent of the base model/GfxObj's own default texture. Read
    /// server-side at render time via Creature_Networking.CalculateObjDesc (Biota.PropertiesTextureMap -> the
    /// outgoing ObjDesc.TextureChanges) - the same lever ACDatRender's --textureSwap exposes for offline preview.
    /// </summary>
    public class TextureMapListing
    {
        [JsonPropertyName("partIndex")]
        public byte PartIndex { get; set; }

        [JsonPropertyName("oldTexture")]
        public uint OldTexture { get; set; }

        [JsonPropertyName("newTexture")]
        public uint NewTexture { get; set; }
    }
}
