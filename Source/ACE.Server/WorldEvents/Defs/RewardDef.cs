using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One entry from Content/events/axes/rewards.json (TECH-DESIGN 5.4).
    /// </summary>
    public class RewardDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("successCrateWcid")]
        public uint SuccessCrateWcid { get; set; }

        [JsonPropertyName("consolationCrateWcid")]
        public uint ConsolationCrateWcid { get; set; }

        /// <summary>
        /// The failure/consolation cache look, and the fallback whenever <see cref="CacheWcids"/> does not
        /// apply: a run that did not end in Success (a FailedTimeout/FailedWipe/FailedNoParticipants outcome)
        /// always spawns THIS wcid, never a pool pick, so the plain chest itself signals "this run did not
        /// succeed" before the player even opens it. Stays REQUIRED: validation still drops the reward entry
        /// when this is 0, so every existing axis file that predates <see cref="CacheWcids"/> is unchanged.
        /// </summary>
        [JsonPropertyName("cacheWcid")]
        public uint CacheWcid { get; set; }

        /// <summary>
        /// Optional pool of alternate cache looks, used ONLY when the run ends in Success. When non-empty,
        /// ONE entry is chosen at random per successful run and every cache that run places uses that same
        /// wcid - a multi-cache site still reads as one reward centre rather than a mismatched row of chests.
        /// A run that fails never draws from this pool regardless of its contents; it always gets
        /// <see cref="CacheWcid"/> instead (see that field's remarks). The pool also falls back to
        /// <see cref="CacheWcid"/> within a successful run if it is empty or every entry fails to resolve to
        /// a real weenie at run time.
        /// </summary>
        [JsonPropertyName("cacheWcids")]
        public List<uint> CacheWcids { get; set; } = new List<uint>();

        [JsonPropertyName("participantsPerCache")]
        public int ParticipantsPerCache { get; set; }

        /// <summary>
        /// WP-18 item 3: place exactly this many caches, whatever the audience size. 0 or missing keeps the
        /// pre-WP-18 behaviour and falls back to <see cref="ParticipantsPerCache"/>, so an axis file that
        /// does not mention the key is unchanged.
        ///
        /// One large chest IS the reward centre: the claim gates (character/account/IP) are per-player and
        /// live on the claim handler, not on the object, so a single cache still serves everyone who
        /// qualifies. Validated to 0..WorldEventRewardDelivery.MaxCachesPerRun; anything else drops the
        /// reward entry with a diagnostic.
        /// </summary>
        [JsonPropertyName("cacheCount")]
        public int CacheCount { get; set; }

        [JsonPropertyName("claimWindowSeconds")]
        public int ClaimWindowSeconds { get; set; }

        /// <summary>
        /// WP-19 item 3: environmental treasure placed around EACH cache when it is spawned - piles, urns,
        /// scattered coin, whatever the scene wants. Empty, the default and the shipped profile's value,
        /// means the cache stands alone exactly as before.
        ///
        /// Reuses <see cref="DecorDef"/> unchanged, so the fields are {wcid, dx, dy, dz, scale, speed, yaw,
        /// inverted} with their usual meanings - except that the offsets are measured from THE CACHE's
        /// position rather than from the geometry centre, and unlike sky decor these ARE snapped to terrain
        /// with dz applied on top (they stand on the ground beside a chest).
        ///
        /// Lifetime is the CACHE's, not the run's: the props survive the creature cleanup pass at Finish
        /// and are destroyed with the caches when the claim window ends. Entries that fail validation are
        /// dropped individually at load; the reward profile itself is always kept.
        /// </summary>
        [JsonPropertyName("cacheDecor")]
        public List<DecorDef> CacheDecor { get; set; } = new List<DecorDef>();

        [JsonPropertyName("gateByCharacter")]
        public bool GateByCharacter { get; set; }

        [JsonPropertyName("gateByAccount")]
        public bool GateByAccount { get; set; }

        [JsonPropertyName("gateByIp")]
        public bool GateByIp { get; set; }

        /// <summary>
        /// The booby prize handed to a claimant with no damage or kill credit on the run. OPTIONAL and
        /// defaults to 0; 0 means the feature is inert for this axis and every claimant gets the ordinary
        /// outcome crate. Deliberately NOT part of the required-wcid validation, so an axis file written
        /// before this field is unchanged and still loads.
        /// </summary>
        [JsonPropertyName("coalWcid")]
        public uint CoalWcid { get; set; }
    }
}
