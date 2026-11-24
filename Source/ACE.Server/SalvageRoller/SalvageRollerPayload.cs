using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The candidate list of one batch, stored as character_salvage_batch.payload (Docs/SalvageRoller/DESIGN.md 5.2):
    ///
    ///   {"v":1,"cands":[{"seq":k,"diff":{...},"summary":{"tier":t,"total":x,"lines":[...]}}]}
    ///
    /// Every candidate is a concrete result, never a seed: the rollers draw from the global ThreadSafeRandom and read
    /// live tunables, so a seed could not be replayed (DESIGN.md section 4). A reader refuses an unknown version, as
    /// DuoGemSnapshot.TryRead does, because a misread payload would apply the wrong result to a real item.
    /// </summary>
    public sealed class SalvageRollerPayload
    {
        public const int SchemaVersion = 1;

        // NO initializers on these two: the deserializer starts from the parameterless constructor, and a default of
        // SchemaVersion or an empty list would let a payload with no "v" or no "cands" read as a valid empty batch.
        [JsonPropertyName("v")] public int Version { get; set; }

        [JsonPropertyName("cands")] public List<SalvageRollerCandidate> Candidates { get; set; }

        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public SalvageRollerPayload()
        {
        }

        public SalvageRollerPayload(IEnumerable<SalvageRollerCandidate> candidates)
        {
            Version = SchemaVersion;
            Candidates = candidates.ToList();
        }

        public string Serialize() => JsonSerializer.Serialize(this, Options);

        /// <summary>
        /// False, with a reason, for null or blank text, malformed JSON, an unknown or missing version, no candidates, a
        /// candidate with no diff or summary, or sequence numbers that are not exactly 1..N (N = the candidate count, in
        /// any array order). Never throws. The take additionally checks N against the batch row's roll_count.
        /// </summary>
        public static bool TryRead(string json, out SalvageRollerPayload payload, out string error)
        {
            payload = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the payload is empty";
                return false;
            }

            SalvageRollerPayload read;

            try
            {
                read = JsonSerializer.Deserialize<SalvageRollerPayload>(json, Options);
            }
            catch (Exception ex)
            {
                error = $"the payload is not valid JSON ({ex.Message})";
                return false;
            }

            if (read == null)
            {
                error = "the payload is empty";
                return false;
            }

            // the version first: a newer server's payload must be named as such, whatever its body looks like
            if (read.Version != SchemaVersion)
            {
                error = $"the payload has schema version {read.Version}, this server reads {SchemaVersion}";
                return false;
            }

            if (read.Candidates == null || read.Candidates.Count == 0)
            {
                error = "the payload has no candidates";
                return false;
            }

            var seen = new HashSet<int>();

            foreach (var candidate in read.Candidates)
            {
                if (candidate == null || candidate.Diff == null || candidate.Summary == null)
                {
                    error = "the payload has a candidate with no diff or summary";
                    return false;
                }

                if (candidate.Seq <= 0 || !seen.Add(candidate.Seq))
                {
                    error = $"the payload has a missing or repeated sequence number {candidate.Seq}";
                    return false;
                }

                candidate.Diff.Normalize();
                candidate.Summary.Lines ??= new List<string>();
            }

            // positive and unique, so this is exactly 1..N unless one is missing and a larger one stands in for it
            var count = read.Candidates.Count;

            if (seen.Max() != count)
            {
                error = $"the payload's sequence numbers are not exactly 1..{count}";
                return false;
            }

            payload = read;
            return true;
        }

        /// <summary>The candidate with sequence number <paramref name="seq"/>, or null.</summary>
        public SalvageRollerCandidate Find(int seq) => Candidates.FirstOrDefault(c => c.Seq == seq);
    }

    /// <summary>One rolled result: its roll-order sequence number (1-based), what it changes, and how it reads.</summary>
    public sealed class SalvageRollerCandidate : ACE.Server.WorldObjects.ISalvageRollerRowCandidate
    {
        [JsonPropertyName("seq")] public int Seq { get; set; }

        // no initializers, for the reason on SalvageRollerPayload.Version: a candidate read with no diff or summary
        // must be refused, not filled with an empty one that would apply "nothing" to a real item
        [JsonPropertyName("diff")] public SalvageRollerDiff Diff { get; set; }

        [JsonPropertyName("summary")] public SalvageRollerSummary Summary { get; set; }
    }

    /// <summary>
    /// How a candidate reads, for sorting and the chat summary at take. Weapons: tier is the WeaponQualityTier and
    /// total the summed special intensity. Obsidian: tier 0 and total the summed mod fractions. Serpentine: both 0,
    /// so roll order is the only order.
    /// </summary>
    public sealed class SalvageRollerSummary
    {
        [JsonPropertyName("tier")] public int Tier { get; set; }

        [JsonPropertyName("total")] public double Total { get; set; }

        [JsonPropertyName("lines")] public List<string> Lines { get; set; } = new List<string>();
    }

    /// <summary>
    /// The difference between a scratch copy of the target before and after one result: per property table, each
    /// changed key with its new value, or null when the result REMOVES that property. Spells: the ids added (with
    /// their cast probability) and the ids removed.
    ///
    /// bools is carried beside the five tables DESIGN.md 5.2 names, so a roller that ever writes a PropertyBool is
    /// recorded rather than silently lost. A change to any other table DuoGemSnapshot captures (the Scribe instance
    /// id, palettes, texture maps, anim parts, enchantments, wcid or weenie type) is refused when the diff is taken -
    /// see SalvageRollerEngine.ComputeDiff. Tables DuoGemSnapshot does not capture (other instance ids, positions,
    /// the placement ints, creature/content tables) are not seen at all; that comment names them.
    /// </summary>
    public sealed class SalvageRollerDiff
    {
        [JsonPropertyName("ints")] public Dictionary<int, int?> Ints { get; set; } = new Dictionary<int, int?>();

        [JsonPropertyName("int64s")] public Dictionary<int, long?> Int64s { get; set; } = new Dictionary<int, long?>();

        [JsonPropertyName("floats")] public Dictionary<int, double?> Floats { get; set; } = new Dictionary<int, double?>();

        [JsonPropertyName("strings")] public Dictionary<int, string> Strings { get; set; } = new Dictionary<int, string>();

        [JsonPropertyName("dids")] public Dictionary<int, uint?> DataIds { get; set; } = new Dictionary<int, uint?>();

        [JsonPropertyName("bools")] public Dictionary<int, bool?> Bools { get; set; } = new Dictionary<int, bool?>();

        [JsonPropertyName("spells")] public SalvageRollerSpellDiff Spells { get; set; } = new SalvageRollerSpellDiff();

        [JsonIgnore]
        public bool IsEmpty =>
            Ints.Count == 0 && Int64s.Count == 0 && Floats.Count == 0 && Strings.Count == 0 && DataIds.Count == 0 &&
            Bools.Count == 0 && Spells.Add.Count == 0 && Spells.Remove.Count == 0;

        /// <summary>Replaces any table a hand-edited or older payload left out with an empty one.</summary>
        internal void Normalize()
        {
            Ints ??= new Dictionary<int, int?>();
            Int64s ??= new Dictionary<int, long?>();
            Floats ??= new Dictionary<int, double?>();
            Strings ??= new Dictionary<int, string>();
            DataIds ??= new Dictionary<int, uint?>();
            Bools ??= new Dictionary<int, bool?>();
            Spells ??= new SalvageRollerSpellDiff();
            Spells.Add ??= new Dictionary<int, float>();
            Spells.Remove ??= new List<int>();
        }
    }

    public sealed class SalvageRollerSpellDiff
    {
        /// <summary>Spell id -> cast probability, for every spell the result adds or whose probability it changes.</summary>
        [JsonPropertyName("add")] public Dictionary<int, float> Add { get; set; } = new Dictionary<int, float>();

        /// <summary>Spell ids the result removes.</summary>
        [JsonPropertyName("remove")] public List<int> Remove { get; set; } = new List<int>();
    }
}
