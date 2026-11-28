using System.Collections.Generic;
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
        public const string KnotPattern = "pattern";
        public const string KnotFiber = "fiber";

        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>
        /// Alternative to <see cref="Wcid"/> (then 0/absent): every time the roller picks this entry it resolves
        /// to ONE member, chosen uniformly, per pick. Not valid with scale "bags".
        /// </summary>
        [JsonPropertyName("wcids")]
        public List<uint> Wcids { get; set; } = new List<uint>();

        /// <summary>Optional 1..5: stamped on every object created for this entry (ItemMaxLevel and the level overlay icon).</summary>
        [JsonPropertyName("itemMaxLevel")]
        public int? ItemMaxLevel { get; set; }

        /// <summary>
        /// Optional "pattern" or "fiber": the created object is a Knot Pattern (stamped with a random available
        /// pinnacle) or a Knot Fiber (stamped with the opening player's level), the way the Thread drop path
        /// stamps them. "pattern" needs wcid KnotItems.PatternWcid, "fiber" needs KnotItems.FiberWcid; not
        /// combinable with wcids or itemMaxLevel.
        /// </summary>
        [JsonPropertyName("knotItem")]
        public string KnotItem { get; set; }

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