using System.Text.Json.Serialization;

namespace ACE.Adapter.GDLE.Models
{
    /// <summary>
    /// A single object-wide sub-palette application (WeeniePropertiesPalette) - writes one concrete palette
    /// (or palette set) over [offset, offset+length) of the composite palette, in the same /8 units the
    /// client protocol uses. On an unequipped creature these are applied via the biota-override ObjDesc path
    /// (Creature_Networking.CalculateObjDesc), alongside animParts.
    /// </summary>
    public class PaletteListing
    {
        [JsonPropertyName("subPaletteId")]
        public uint SubPaletteId { get; set; }

        [JsonPropertyName("offset")]
        public ushort Offset { get; set; }

        [JsonPropertyName("length")]
        public ushort Length { get; set; }
    }
}
