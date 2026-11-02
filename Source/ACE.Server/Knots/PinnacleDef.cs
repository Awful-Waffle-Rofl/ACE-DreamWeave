using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACE.Server.Knots
{
    /// <summary>
    /// Root of Content/dungeons/dynamic/pinnacles.json. Optional, like clearance.json and puzzle-gates.json: a
    /// missing, malformed or unsupported-version file means "no pinnacles", never a refused store. Each row is
    /// kept as a raw <see cref="JsonElement"/> so one wrongly typed pinnacle costs only that pinnacle.
    /// </summary>
    public class PinnaclesFileDef
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("pinnacles")]
        public List<JsonElement> Pinnacles { get; set; } = new List<JsonElement>();
    }

    /// <summary>
    /// One pinnacle boss (Docs/Knots/KNOTS-DESIGN.md section 8). Adding a pinnacle adds one of these, its
    /// content and its rewards - no code. The shape is the contract; the encounter that satisfies it is built
    /// elsewhere and registered for <see cref="PinnacleDestinationDef.Kind"/>.
    /// </summary>
    public sealed record PinnacleDef
    {
        /// <summary>Stable id, also what a Pattern names (PropertyString PinnacleKey).</summary>
        [JsonPropertyName("key")]
        public string Key { get; init; }

        /// <summary>Display name used on bound Knots and Patterns.</summary>
        [JsonPropertyName("name")]
        public string Name { get; init; }

        [JsonPropertyName("destination")]
        public PinnacleDestinationDef Destination { get; init; }

        /// <summary>Lives per Knot.</summary>
        [JsonPropertyName("entries")]
        public int Entries { get; init; } = KnotItems.DefaultEntries;

        /// <summary>
        /// Phase start thresholds as fractions of boss health, strictly descending in (0, 1], starting at 1.0
        /// (for example 1.0, 0.66, 0.33).
        /// </summary>
        [JsonPropertyName("phases")]
        public List<double> Phases { get; init; } = new List<double> { 1.0 };

        /// <summary>Reference (key or name) to the pinnacle's curated reward table. Opaque to the registry.</summary>
        [JsonPropertyName("rewardTable")]
        public string RewardTable { get; init; }

        [JsonPropertyName("rollBand")]
        public RollBandDef RollBand { get; init; } = new RollBandDef();

        /// <summary>1 in v1; reserved for group Knots.</summary>
        [JsonPropertyName("partyMax")]
        public int PartyMax { get; init; } = 1;

        /// <summary>A disabled pinnacle is excluded from random selection and Pattern drops.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Names the encounter kind (what <c>PinnacleEncounters.Register</c> is keyed on) and carries the
    /// kind-specific placement as free-form siblings of "kind" (for example landblock, entry, bossWcid).
    /// </summary>
    public sealed class PinnacleDestinationDef
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Data { get; set; } = new Dictionary<string, JsonElement>();
    }

    /// <summary>
    /// Gear mod roll band endpoints: at <see cref="LowLevel"/> and below the band is <see cref="LowBand"/>, at
    /// <see cref="HighLevel"/> it is <see cref="HighBand"/>, linear between and beyond, with the band's low
    /// edge stopping at <see cref="ClampLow"/> and its high edge at 100. Defaults are the design's: 185 ->
    /// 1-40, 375 -> 60-100, clamp 80.
    /// </summary>
    public sealed record RollBandDef
    {
        [JsonPropertyName("lowLevel")]
        public int LowLevel { get; init; } = 185;

        [JsonPropertyName("lowBand")]
        public int[] LowBand { get; init; } = { 1, 40 };

        [JsonPropertyName("highLevel")]
        public int HighLevel { get; init; } = 375;

        [JsonPropertyName("highBand")]
        public int[] HighBand { get; init; } = { 60, 100 };

        [JsonPropertyName("clampLow")]
        public int ClampLow { get; init; } = 80;
    }

    /// <summary>
    /// Pure roll-band math shared by every pinnacle's gear (Docs/Knots/KNOTS-DESIGN.md section 7).
    /// </summary>
    public static class PinnacleRollBand
    {
        /// <summary>Hard ceiling of either band edge, in percent.</summary>
        public const double Ceiling = 100.0;

        /// <summary>
        /// The band (percent) for a Knot level: each edge is linear in level through the two anchor points and
        /// extrapolated both ways; the low edge is then clamped to [lowBand.low, clampLow] and the high edge to
        /// [lowBand.high, 100].
        /// </summary>
        public static (double Low, double High) Band(int level, RollBandDef def)
        {
            def ??= new RollBandDef();

            double lowAnchorLow = def.LowBand[0], lowAnchorHigh = def.LowBand[1];
            double highAnchorLow = def.HighBand[0], highAnchorHigh = def.HighBand[1];

            double span = def.HighLevel - def.LowLevel;
            double t = span > 0 ? (level - def.LowLevel) / span : 0.0;

            double low = lowAnchorLow + t * (highAnchorLow - lowAnchorLow);
            double high = lowAnchorHigh + t * (highAnchorHigh - lowAnchorHigh);

            low = Math.Min(Math.Max(low, lowAnchorLow), Math.Max(def.ClampLow, lowAnchorLow));
            high = Math.Min(Math.Max(high, lowAnchorHigh), Math.Max(Ceiling, lowAnchorHigh));

            if (high < low) high = low;

            return (low, high);
        }

        /// <summary>One uniform roll inside the band, in percent. <paramref name="nextDouble"/> returns [0, 1).</summary>
        public static double Roll((double Low, double High) band, Func<double> nextDouble)
            => band.Low + nextDouble() * (band.High - band.Low);

        public static double Roll((double Low, double High) band, Random rng)
            => Roll(band, rng.NextDouble);
    }
}
