using System.Text.Json.Serialization;

namespace ACE.Adapter.GDLE.Models
{
    /// <summary>
    /// A single body-part model override (WeeniePropertiesAnimPart) - swaps the GfxObj rendered at one
    /// Setup part index for another, independent of the base Setup's own default part list.
    /// </summary>
    public class AnimPartListing
    {
        [JsonPropertyName("index")]
        public byte Index { get; set; }

        [JsonPropertyName("animationId")]
        public uint AnimationId { get; set; }
    }
}
