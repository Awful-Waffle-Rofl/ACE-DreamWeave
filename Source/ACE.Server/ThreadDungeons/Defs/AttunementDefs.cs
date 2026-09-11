using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.ThreadDungeons.Defs
{
    /// <summary>
    /// Op vocabulary for Content/dungeons/dynamic/attunement.json. Semantics live in RawFragmentRules (D3);
    /// this class carries only the string constants and the two membership sets the loader's lint needs.
    /// </summary>
    public static class AttunementOps
    {
        public const string AddOrRaise = "add_or_raise";
        public const string RaiseRandom = "raise_random";
        public const string RaiseAll = "raise_all";
        public const string SetMax = "set_max";
        public const string RemoveOne = "remove_one";
        public const string RerollOne = "reroll_one";
        public const string LockOne = "lock_one";
        public const string LevelUp = "level_up";
        public const string AddEntry = "add_entry";
        public const string Nothing = "nothing";

        // ---- press v2 (owner ruling, 2026-09-07) -----------------------------------------------------

        /// <summary>
        /// add_or_raise over a modifier the op does NOT name: one is drawn from the op's
        /// <see cref="OpDef.Scope"/> pool. The Turquoise Taper's "aim roughly" op.
        /// </summary>
        public const string AddRandom = "add_random";

        /// <summary>
        /// Raises the magnitude of a modifier ALREADY on the board, by the op's band read as a fraction of
        /// the modifier's own range. Adds nothing. The talisman slot's op.
        /// </summary>
        public const string Sharpen = "sharpen";

        /// <summary>
        /// Re-rolls the magnitude of the <see cref="OpDef.Amount"/> weakest unlocked modifiers on the board,
        /// inside the op's band. The modifier ids are unchanged - this refines what is there rather than
        /// replacing it. The potion slot's op.
        /// </summary>
        public const string RerollWeakest = "reroll_weakest";

        /// <summary>
        /// Sets the run's difficulty in one step: <see cref="OpDef.Amount"/> is a LEVEL offset (not a rung of
        /// ten, unlike the retired level_up) applied to the fragment's own level, and
        /// <see cref="OpDef.Entries"/> SETS the entry count. One op rather than two because a component draws
        /// exactly ONE op from its list, so a scarab that had to move both would only ever move one of them.
        /// The scarab slot's op.
        /// </summary>
        public const string SetDifficulty = "set_difficulty";

        // "calm" and "zero_instability" were removed with the instability mechanic (owner ruling,
        // 2026-09-07). They are deliberately NOT kept as no-op constants: leaving them in Known would let
        // attunement.json declare a component whose only op does nothing at all, which is the exact failure
        // the ruling removed five components to avoid. A file still carrying one now fails lint rule 11 and
        // the component is dropped with a diagnostic, which is loud rather than silent.

        // WHY THE v1 OPS SURVIVE PRESS v2 (owner ruling, 2026-09-07). No shipped component uses
        // raise_random, raise_all, set_max, remove_one, reroll_one, lock_one, level_up or add_entry any more
        // - the glyph, ink and quill types that carried most of them retired, and the scarab, potion and
        // talisman types moved to the four ops above. They are kept IMPLEMENTED and in Known deliberately,
        // and this is not the same call as the deleted RenderEffectRange formatter: these are data-driven
        // ops, re-enabled by editing one JSON row rather than by writing code, they are still covered by
        // RawFragmentRulesTests, and lock_one in particular remains meaningful because locks still exist on
        // the spec (DungeonGemSpec.MaxLocks) and are still honoured by reroll_weakest and remove_one.
        // Retiring them from Known would additionally make every pre-v2 attunement.json fail lint rather
        // than merely go unused.

        public static readonly IReadOnlySet<string> Known = new HashSet<string>
        {
            AddOrRaise, RaiseRandom, RaiseAll, SetMax, RemoveOne, RerollOne,
            LockOne, LevelUp, AddEntry, Nothing,
            AddRandom, Sharpen, RerollWeakest, SetDifficulty,
        };

        /// <summary>
        /// Ops whose "modifier" argument is required on a component op. <see cref="Sharpen"/> is deliberately
        /// absent: its modifier is a PREFERENCE (the talisman's own wood), and an untargeted sharpen - Oak and
        /// Hemlock - is a shipped, intended shape rather than a content bug.
        /// </summary>
        public static readonly IReadOnlySet<string> RequireModifierArg = new HashSet<string>
        {
            AddOrRaise, SetMax, LockOne,
        };
    }

    /// <summary>
    /// The <see cref="OpDef.Scope"/> vocabulary: which modifiers an op that does not name one may reach.
    /// Every value is resolved through <see cref="DungeonModifierCategories"/> rather than through a private
    /// list of ids, so a new modifiers.json row lands in the right pool the moment it is added.
    /// </summary>
    public static class AttunementScopes
    {
        /// <summary>Category "monster" only.</summary>
        public const string Monster = "monster";

        /// <summary>Category "boss" only.</summary>
        public const string Boss = "boss";

        /// <summary>
        /// Monster AND boss - the taper slots' own budget category, which is what the Turquoise Taper draws
        /// from. Named for the role rather than as "monster_or_boss" because that is what it means to a
        /// player: the difficulty knobs, as opposed to the reward ones.
        /// </summary>
        public const string Difficulty = "difficulty";

        /// <summary>
        /// The salvage affinities - the powder slot's own budget category, and what a WILDCARD powder draws
        /// from. Owner ruling 2026-09-07: a powder aligns to a material from a list the owner chooses rather
        /// than to the gem its name comes from, and the powders left without a mapping roll at random over
        /// this scope. Resolved through RawFragmentRules.BudgetOf, so a ninth affinity row is reachable by
        /// the wildcards the moment it is added - nothing anywhere counts them.
        /// </summary>
        public const string Salvage = "salvage";

        /// <summary>Everything. Also what an omitted scope means.</summary>
        public const string Any = "any";

        public static readonly IReadOnlySet<string> Known = new HashSet<string> { Monster, Boss, Difficulty, Salvage, Any };
    }

    /// <summary>Root of Content/dungeons/dynamic/attunement.json.</summary>
    public class AttunementFileDef
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("colors")]
        public List<ColorMapDef> Colors { get; set; } = new List<ColorMapDef>();

        [JsonPropertyName("components")]
        public List<ComponentDef> Components { get; set; } = new List<ComponentDef>();

        // The "wild" block went with the instability mechanic (owner ruling, 2026-09-07): a press now applies
        // the loaded components' own ops and nothing else. System.Text.Json ignores unknown properties, so a
        // file still carrying a "wild" array loads fine and the array is simply not read.
    }

    public class ColorMapDef
    {
        [JsonPropertyName("color")]
        public string Color { get; set; }

        /// <summary>Modifier id this colour locks, or null for turquoise (the re-roll colour).</summary>
        [JsonPropertyName("modifier")]
        public string Modifier { get; set; }
    }

    public class ComponentDef
    {
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// One of "scarab", "herb", "powder", "taper", "potion", "talisman" - the six press v2 slot types
        /// (owner ruling, 2026-09-07). "glyph", "ink" and "quill" retired with that ruling and are no longer
        /// valid: a file still carrying one now fails lint rule 5 and the component is dropped with a
        /// diagnostic, which is loud rather than silent.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("color")]
        public string Color { get; set; }

        [JsonPropertyName("dose")]
        public int Dose { get; set; } = 1;

        /// <summary>Per-pressing cap for the component's TYPE, not the individual wcid; every entry of a type must agree.</summary>
        [JsonPropertyName("limit")]
        public int Limit { get; set; } = 1;

        // The per-component "instability" cost went with the instability mechanic (owner ruling,
        // 2026-09-07). As with the file-level "wild" block, an unknown JSON property is ignored rather than
        // rejected, so a stale file still carrying the field loads unchanged.

        [JsonPropertyName("ops")]
        public List<OpDef> Ops { get; set; } = new List<OpDef>();
    }

    public class OpDef
    {
        [JsonPropertyName("op")]
        public string Op { get; set; }

        /// <summary>
        /// Modifier id argument; REQUIRED for add_or_raise/set_max/lock_one, OPTIONAL for sharpen (where it
        /// is the preferred target and the op falls back to its scope when that modifier is absent), and
        /// unread by every other op.
        /// </summary>
        [JsonPropertyName("modifier")]
        public string Modifier { get; set; }

        /// <summary>
        /// Read by "level_up" (rungs of 10 levels), by "set_difficulty" (a LEVEL offset, signed) and by
        /// "reroll_weakest" (how many of the board's weakest modifiers to re-roll).
        /// </summary>
        [JsonPropertyName("amount")]
        public int Amount { get; set; }

        /// <summary>
        /// Only read by "set_difficulty": the entry count the pressed gem is SET to (not raised by), clamped
        /// to 1..RawFragmentRules.MaxEntries. 0 means "leave the entry count alone", which is what every op
        /// that does not carry the field does.
        /// </summary>
        [JsonPropertyName("entries")]
        public int Entries { get; set; }

        /// <summary>
        /// Which modifiers an op that does not NAME one may reach - one of
        /// <see cref="AttunementScopes"/>. Read by "add_random" and by an untargeted "sharpen". Omitted
        /// means <see cref="AttunementScopes.Any"/>.
        /// </summary>
        [JsonPropertyName("scope")]
        public string Scope { get; set; }

        /// <summary>
        /// The BAND, lower edge. Both edges are FRACTIONS in [0, 1] of the modifier's own declared
        /// minMagnitude..maxMagnitude range, never absolute magnitudes, so retuning a modifier's range
        /// retunes every component that points at it and nothing silently falls outside:
        ///
        ///     rolled = minMagnitude + (maxMagnitude - minMagnitude) * uniform(bandMin, bandMax)
        ///
        /// This is the primitive that makes ~87 components distinct without ~87 modifier rows: a component is
        /// deterministic in WHICH modifier it names and its band decides both how strong it is and how swingy.
        /// The default 0.0-1.0 is the pre-v2 behaviour of rolling across the whole range.
        ///
        /// The band is inert on a modifier whose min equals its max (the "hollow" row is the shipped case):
        /// there is nothing to interpolate, so the roll is still taken - the draw order must not depend on
        /// the data - and lands on the single value.
        /// </summary>
        [JsonPropertyName("bandMin")]
        public double BandMin { get; set; } = 0.0;

        /// <summary>The BAND, upper edge. See <see cref="BandMin"/>.</summary>
        [JsonPropertyName("bandMax")]
        public double BandMax { get; set; } = 1.0;

        [JsonPropertyName("weight")]
        public int Weight { get; set; } = 1;
    }

    /// <summary>
    /// Parsed, validated snapshot of attunement.json. Empty is the always-present fallback so a missing or
    /// broken file refuses every fragment operation instead of throwing.
    /// </summary>
    public sealed class AttunementDef
    {
        public static readonly AttunementDef Empty = new AttunementDef(
            new Dictionary<uint, ComponentDef>(), new Dictionary<string, string>());

        public IReadOnlyDictionary<uint, ComponentDef> Components { get; }

        /// <summary>Colour name -> modifier id. Turquoise (the re-roll colour) is absent, never mapped to null.</summary>
        public IReadOnlyDictionary<string, string> ColorModifier { get; }

        public AttunementDef(IReadOnlyDictionary<uint, ComponentDef> components, IReadOnlyDictionary<string, string> colorModifier)
        {
            Components = components;
            ColorModifier = colorModifier;
        }

        public bool TryGet(uint wcid, out ComponentDef component) => Components.TryGetValue(wcid, out component);

        /// <summary>The component's authored display name, or "wcid &lt;n&gt;" when the wcid is unknown.</summary>
        public string DisplayName(uint wcid) => Components.TryGetValue(wcid, out var component) ? component.Name : $"wcid {wcid}";
    }
}
