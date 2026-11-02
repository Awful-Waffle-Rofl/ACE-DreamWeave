using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One weighted entry of a reward's presence-payout pool (<see cref="RewardDef.Pool"/>). A full roll
    /// gives <see cref="Qty"/> of <see cref="Wcid"/>; a fractional roll scales that by <see cref="Scale"/>.
    /// </summary>
    public class RewardPoolEntryDef
    {
        public const string ScaleStack = "stack";
        public const string ScaleChance = "chance";
        public const string ScaleBags = "bags";

        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        [JsonPropertyName("qty")]
        public int Qty { get; set; }

        [JsonPropertyName("weight")]
        public double Weight { get; set; }

        /// <summary>
        /// How a fractional roll scales this entry: "stack" scales the quantity, "chance" awards the whole
        /// quantity with probability f, "bags" pays Full Bags of <see cref="BagWcid"/> instead of the item.
        /// </summary>
        [JsonPropertyName("scale")]
        public string Scale { get; set; }

        /// <summary>Required when <see cref="Scale"/> is "bags": the wcid of one Full Bag.</summary>
        [JsonPropertyName("bagWcid")]
        public uint? BagWcid { get; set; }

        /// <summary>Full Bags that equal one whole item, for "bags" entries.</summary>
        [JsonPropertyName("bagsPerUnit")]
        public int BagsPerUnit { get; set; } = 10;
    }
}