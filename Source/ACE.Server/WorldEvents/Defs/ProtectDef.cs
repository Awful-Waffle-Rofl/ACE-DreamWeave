using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// The "protect" block of a Hold goal in goals.json: the friendly targets a run spawns near its anchor
    /// and the players must keep alive until the goal's holdSeconds have passed.
    ///
    /// The targets are a property of the GOAL, not of a source theme, so any theme that lists the goal in
    /// its compatibleGoals gets the same targets. Validated at load by WorldEventAxisStore.ParseGoals; a Hold
    /// goal with a missing or invalid block is dropped with a diagnostic.
    /// </summary>
    public class ProtectDef
    {
        /// <summary>
        /// The hard ceiling on how many targets one run may place. count.cap above this is refused at load.
        /// Raise it here (and only here) if a later goal wants more.
        /// </summary>
        public const int MaxProtectedTargets = 3;

        public const int DefaultMinSurvivors = 1;
        public const double DefaultSeekShare = 0.3;
        public const string DefaultNoun = "ward";
        public const string DefaultNounPlural = "wards";

        /// <summary>The Creature weenie placed as each target. Non-Creature wcids are refused at spawn.</summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>
        /// Optional: several Creature weenies, one per target in turn (target i gets wcids[i % count]), so the
        /// targets do not all look alike. Absent or empty uses <see cref="Wcid"/> for every target.
        /// </summary>
        [JsonPropertyName("wcids")]
        public List<uint> Wcids { get; set; }

        /// <summary>The weenie target <paramref name="index"/> is built from: <see cref="Wcids"/> in turn, else <see cref="Wcid"/>.</summary>
        public uint WcidFor(int index)
        {
            if (Wcids != null && Wcids.Count > 0)
            {
                var pick = Wcids[((index % Wcids.Count) + Wcids.Count) % Wcids.Count];

                if (pick != 0)
                    return pick;
            }

            return Wcid;
        }

        /// <summary>
        /// Optional scene props placed around the ANCHOR when the run starts (a campfire, say). Ordinary decor
        /// entries: dx metres east, dy north, dz up, yaw, scale, speed, snap. Placed stuck, never attackable,
        /// removed at the end of the run.
        /// </summary>
        [JsonPropertyName("decor")]
        public List<DecorDef> Decor { get; set; }

        /// <summary>
        /// Optional scene props placed around EACH target, in that target's own frame: dx metres to its right,
        /// dy metres forward (outward, the way it faces), yaw relative to its facing. One list gives every
        /// merchant its own wagon whatever the count or radius (WorldEventHoldRules.PlaceTargetDecor).
        /// </summary>
        [JsonPropertyName("targetDecor")]
        public List<DecorDef> TargetDecor { get; set; }

        /// <summary>How many targets to place, resolved once from the audience when the run goes Active.</summary>
        [JsonPropertyName("count")]
        public ScaledCount Count { get; set; }

        /// <summary>
        /// Each target's maximum health, resolved once from the audience when the run goes Active. ONLY used
        /// when <see cref="UndefendedSeconds"/> is 0 (monsters deal their own raw damage). With time-based
        /// damage it does nothing and is ignored (see <see cref="IsTimeScaled"/>); the loader says so if a
        /// goal sets both, and does not require it.
        /// </summary>
        [JsonPropertyName("health")]
        public ScaledCount Health { get; set; }

        /// <summary>
        /// Metres from the anchor. Targets are spread evenly on a ring of this radius; a single target is
        /// placed at the anchor itself.
        /// </summary>
        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        /// <summary>
        /// Optional: metres from the anchor that this goal's WAVES spawn at, replacing the source theme's
        /// geometryRadius for Hold runs only. 0 or absent (the default) keeps the source's radius. Lets a
        /// Hold run bring the attackers in close enough to reach the targets.
        /// </summary>
        [JsonPropertyName("spawnRadius")]
        public double SpawnRadius { get; set; }

        /// <summary>
        /// Optional: how many seconds ONE of the run's monsters, hitting a target continuously, takes to
        /// destroy it - several attackers proportionally faster. When positive, a monster's hit on a target
        /// takes a slice of the target's max health sized to the time since that monster's previous hit,
        /// whatever its weapon damage, so targets are threatened at any player level (WorldEventHoldRules.
        /// ProtectedHitDamage). 0 or absent (the default) leaves monster damage unscaled.
        /// </summary>
        [JsonPropertyName("undefendedSeconds")]
        public double UndefendedSeconds { get; set; }

        /// <summary>
        /// Optional: the fastest a single target may be destroyed, in seconds, however many monsters are on
        /// it. Caps how much health one target can lose per second at maxHealth / minDeathSeconds (with a
        /// couple of seconds of burst), so stacked attackers cannot melt it, while a target nobody defends still
        /// dies. 0 or absent (the default) means no cap.
        /// </summary>
        [JsonPropertyName("minDeathSeconds")]
        public double MinDeathSeconds { get; set; }

        public const double DefaultMinSeconds = 300;
        public const double DefaultMaxHealPerTenSeconds = 0.10;

        /// <summary>
        /// Optional: wave-based hold. How many waves the group must survive (base / perParticipant / cap,
        /// sized at Active and raised, never lowered, as more players arrive). When set, the hold succeeds the
        /// moment the LAST monster of the last wave dies with enough targets standing - once at least
        /// <see cref="MinSeconds"/> have passed; until then extra waves keep coming. The final counted wave is
        /// a little bigger. Absent keeps the time-only hold (success at holdSeconds). holdSeconds stays the
        /// safety cap either way.
        /// </summary>
        [JsonPropertyName("waves")]
        public ScaledCount Waves { get; set; }

        /// <summary>Wave-based holds only: the hold never succeeds before this many seconds; waves keep coming until then.</summary>
        [JsonPropertyName("minSeconds")]
        public double MinSeconds { get; set; } = DefaultMinSeconds;

        /// <summary>
        /// Optional: fraction (0 to 1) of max health every surviving target regains each time a wave is
        /// fully cleared. 0 (the default) heals nothing.
        /// </summary>
        [JsonPropertyName("healBetweenWaves")]
        public double HealBetweenWaves { get; set; }

        /// <summary>
        /// Players may heal targets (heal spells and health kits): each heal becomes a share of max health,
        /// one typical player heal giving WorldEventHoldRules.HealShareOfTypicalHeal, capped at this fraction
        /// of max health per target per ten seconds. 0 turns player healing of targets off. Default 0.10.
        /// </summary>
        [JsonPropertyName("maxHealPerTenSeconds")]
        public double MaxHealPerTenSeconds { get; set; } = DefaultMaxHealPerTenSeconds;

        /// <summary>True when the hold counts waves (<see cref="Waves"/> is set).</summary>
        [JsonIgnore]
        public bool IsWaveBased => Waves != null;

        /// <summary>
        /// The max health every target is given while damage is time-based (<see cref="UndefendedSeconds"/>
        /// above 0). The number itself changes nothing about how long a target lasts - each hit is a share of
        /// the max - it only has to be large enough that a hit's share never rounds away. <see cref="Health"/>
        /// is not used in that case.
        /// </summary>
        public const int TimeScaledMaxHealth = 10000;

        /// <summary>
        /// True when damage to the targets is time-based, which makes <see cref="Health"/> irrelevant: a
        /// target's lifetime depends on undefendedSeconds and minDeathSeconds alone.
        /// </summary>
        [JsonIgnore]
        public bool IsTimeScaled => double.IsFinite(UndefendedSeconds) && UndefendedSeconds > 0;

        /// <summary>
        /// The run fails as soon as fewer than this many targets are alive. 1 (the default) means "fail when
        /// all are dead". Clamped at runtime to the number of targets actually placed.
        /// </summary>
        [JsonPropertyName("minSurvivors")]
        public int MinSurvivors { get; set; } = DefaultMinSurvivors;

        /// <summary>
        /// The fraction (0 to 1) of every wave's monsters that go straight for the targets instead of the
        /// players. The rest never target them.
        /// </summary>
        [JsonPropertyName("seekShare")]
        public double SeekShare { get; set; } = DefaultSeekShare;

        /// <summary>Broadcast a line as each target first drops to 75%, 50% and 25% health.</summary>
        [JsonPropertyName("healthMilestones")]
        public bool HealthMilestones { get; set; } = true;

        /// <summary>What one target is called in player-facing text, and its plural.</summary>
        [JsonPropertyName("noun")]
        public string Noun { get; set; }

        [JsonPropertyName("nounPlural")]
        public string NounPlural { get; set; }

        /// <summary>
        /// Optional lines the targets speak (local chat), one picked at random each time. Absent or empty pools
        /// say nothing. See <see cref="ProtectLinesDef"/> for the moments and the tokens.
        /// </summary>
        [JsonPropertyName("lines")]
        public ProtectLinesDef Lines { get; set; }

        /// <summary><see cref="SeekShare"/>, or world_events_hold_seek_share when that property is set.</summary>
        [JsonIgnore]
        public double EffectiveSeekShare
        {
            get
            {
                var share = WorldEventOverrides.Double("world_events_hold_seek_share", SeekShare);

                if (!double.IsFinite(share) || share < 0)
                    return 0;

                return share > 1 ? 1 : share;
            }
        }

        [JsonIgnore]
        public string EffectiveNoun => !string.IsNullOrWhiteSpace(Noun) ? Noun : DefaultNoun;

        [JsonIgnore]
        public string EffectiveNounPlural => !string.IsNullOrWhiteSpace(NounPlural) ? NounPlural : DefaultNounPlural;

        /// <summary>The number of targets to place for <paramref name="participants"/> players, within [1, MaxProtectedTargets].</summary>
        public int ResolveCount(int participants)
        {
            var count = Count?.Resolve(Math.Max(0, participants)) ?? 1;

            return Math.Clamp(count, 1, MaxProtectedTargets);
        }

        /// <summary>
        /// The max health a target is actually given: <see cref="TimeScaledMaxHealth"/> while damage is
        /// time-based, otherwise <see cref="ResolveHealth"/>.
        /// </summary>
        public int EffectiveMaxHealth(int participants)
        {
            return IsTimeScaled ? TimeScaledMaxHealth : ResolveHealth(participants);
        }

        /// <summary>The protect.health ScaledCount for <paramref name="participants"/> players. Never below 1. Raw-damage goals only.</summary>
        public int ResolveHealth(int participants)
        {
            var hp = Health?.Resolve(Math.Max(0, participants)) ?? 1;

            return Math.Max(1, hp);
        }
    }

    /// <summary>
    /// The protect block's "lines": what the targets say, by moment. Each is a pool; one line is drawn at
    /// random each time. Every line is spoken in local chat by the target itself; destroyed and success
    /// lines are also copied to the global World Event channel.
    ///
    /// The monster family varies per run, so a line never names one. Tokens, filled per run
    /// (WorldEventHoldRules.FillLine):
    ///   {family}  the run's monster family display name as composed, e.g. "the Emberwrought" or
    ///             "the Blackwing and the Drudges";
    ///   {Family}  the same with its first letter capitalized, for the start of a sentence;
    ///   {noun} {nounPlural} {anchor} {alive} {total}, and {pct} (health lines only).
    /// Player-facing, so ASCII only: an em or en dash is repaired to a hyphen at load.
    /// </summary>
    public class ProtectLinesDef
    {
        /// <summary>Said once by each target as it is placed.</summary>
        [JsonPropertyName("spawn")]
        public List<string> Spawn { get; set; } = new List<string>();

        /// <summary>Said when a target first drops to 75%, 50% and 25% health.</summary>
        [JsonPropertyName("health")]
        public List<string> Health { get; set; } = new List<string>();

        /// <summary>A target's last words as it is destroyed. Also sent globally.</summary>
        [JsonPropertyName("destroyed")]
        public List<string> Destroyed { get; set; } = new List<string>();

        /// <summary>Said by one surviving target when the hold succeeds. Also sent globally.</summary>
        [JsonPropertyName("success")]
        public List<string> Success { get; set; } = new List<string>();

        /// <summary>Said by one surviving target when the targets heal between waves (protect.healBetweenWaves).</summary>
        [JsonPropertyName("heal")]
        public List<string> Heal { get; set; } = new List<string>();

        /// <summary>Every pool, with its goals.json key, for load-time repair and diagnostics.</summary>
        [JsonIgnore]
        public IEnumerable<(string Key, List<string> Pool)> Pools => new[]
        {
            ("spawn", Spawn), ("health", Health), ("destroyed", Destroyed), ("success", Success), ("heal", Heal)
        };
    }
}
