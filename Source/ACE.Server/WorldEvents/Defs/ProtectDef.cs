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
        /// target its own props whatever the count or radius (WorldEventHoldRules.PlaceTargetDecor).
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
        /// Optional: seconds taken off <see cref="UndefendedSeconds"/> for every participant beyond the first, down to
        /// <see cref="UndefendedSecondsFloor"/>. A bigger group kills each attacker sooner, so each one has to hit
        /// harder for the targets to stay under threat. 0 (the default) keeps one value for every group size.
        /// </summary>
        [JsonPropertyName("undefendedSecondsPerParticipant")]
        public double UndefendedSecondsPerParticipant { get; set; }

        /// <summary>The lowest <see cref="UndefendedSeconds"/> can go with <see cref="UndefendedSecondsPerParticipant"/> (0 = no floor).</summary>
        [JsonPropertyName("undefendedSecondsFloor")]
        public double UndefendedSecondsFloor { get; set; }

        /// <summary>
        /// <see cref="UndefendedSeconds"/> for <paramref name="participants"/> players (D6 - pure): less
        /// <see cref="UndefendedSecondsPerParticipant"/> per player beyond the first, never under
        /// <see cref="UndefendedSecondsFloor"/> or 1. A solo staff test counts as one.
        /// </summary>
        public double EffectiveUndefendedSeconds(int participants)
        {
            if (!(UndefendedSeconds > 0))
                return UndefendedSeconds;

            var extra = Math.Max(0, Math.Max(1, participants) - 1);
            var seconds = UndefendedSeconds - Math.Max(0, UndefendedSecondsPerParticipant) * extra;

            return Math.Max(Math.Max(1, UndefendedSecondsFloor), Math.Min(UndefendedSeconds, seconds));
        }

        /// <summary>
        /// Optional: the fastest a single target may be destroyed, in seconds, however many monsters are on
        /// it. Caps how much health one target can lose per second at maxHealth / minDeathSeconds (with a
        /// couple of seconds of burst), so stacked attackers cannot melt it, while a target nobody defends still
        /// dies. 0 or absent (the default) means no cap.
        /// </summary>
        [JsonPropertyName("minDeathSeconds")]
        public double MinDeathSeconds { get; set; }

        public const double DefaultMinSeconds = 180;
        public const double DefaultMaxHealPerTenSeconds = 0.10;

        public const double DefaultNextWaveAliveShare = 0.25;
        public const double DefaultMinWaveGapSeconds = 8;
        public const double DefaultFinaleBossScale = 1.75;
        public const double DefaultFinaleBossHealthMult = 3.0;
        public const string DefaultBossEffect = "ShieldUpRed";
        public const double DefaultBossEffectSeconds = 3;
        public const string DefaultBossArrivalEffect = "LevelUp";
        public const string DefaultFinaleBossPrefix = "Warlord";
        public const double DefaultFinaleBossLeadSeconds = 5;

        /// <summary>
        /// Whether players see a health bar when they select a target. The client only draws one for a creature it
        /// is told is attackable, which also makes its name red, so true (the default) sends targets to the client
        /// as attackable. The server still refuses every player and pet attack on them either way. False keeps the
        /// friendly (non-red) name with no bar.
        /// </summary>
        [JsonPropertyName("showHealthBar")]
        public bool ShowHealthBar { get; set; } = true;

        /// <summary>
        /// Optional, wave-based holds: every wave except the finale carries all the roles together (breach squad,
        /// runners, ranged, hunters, seekers), in these shares. When set, the one-type-per-wave rolls
        /// (specialWaveChance, minSpecialWaves) are not used; --wave-type still forces a one-type wave for testing.
        /// </summary>
        [JsonPropertyName("waveMix")]
        public WaveMixDef WaveMix { get; set; }

        // ---- keeping the targets alive (all off by default) ----------------------------------------------

        /// <summary>Share of max health (0 to 1) every standing target regains when a corrupted monster dies. 0 = off.</summary>
        [JsonPropertyName("corruptedKillHeal")]
        public double CorruptedKillHeal { get; set; }

        /// <summary>Share of max health (0 to 1) a target regains when a player uses its shrine. Needs <see cref="Shrine"/>.</summary>
        [JsonPropertyName("shrineHeal")]
        public double ShrineHeal { get; set; } = 0.15;

        /// <summary>Seconds a shrine rests after a use (1 to 600). Default 45.</summary>
        [JsonPropertyName("shrineCooldownSeconds")]
        public double ShrineCooldownSeconds { get; set; } = 45;

        /// <summary>Share of max health (0 to 1) a target regains when a monster that was attacking it is killed. 0 = off.</summary>
        [JsonPropertyName("rescueHeal")]
        public double RescueHeal { get; set; }

        /// <summary>The most a target can regain from rescues per wave (0 to 1). Default 0.10.</summary>
        [JsonPropertyName("rescueCapPerWave")]
        public double RescueCapPerWave { get; set; } = 0.10;

        /// <summary>A target regenerates only with no hostile wave monster within this many metres (0 to 50). Default 10.</summary>
        [JsonPropertyName("clearRadius")]
        public double ClearRadius { get; set; } = 10;

        /// <summary>Share of max health (0 to 0.1) a clear target regenerates each second. 0 = off.</summary>
        [JsonPropertyName("clearRegenPerSecond")]
        public double ClearRegenPerSecond { get; set; }

        /// <summary>
        /// The most a target can regain per wave (0 to 1) across every heal source together: shrine, corrupted kills,
        /// rescues, regeneration, the between-waves heal and player heals. Once spent, healing it does nothing until
        /// the next wave starts. 0 = no budget.
        /// </summary>
        [JsonPropertyName("healBudgetPerWave")]
        public double HealBudgetPerWave { get; set; }

        /// <summary>Optional: a restoration shrine beside each target (see ShrineDef). Absent: no shrines.</summary>
        [JsonPropertyName("shrine")]
        public ShrineDef Shrine { get; set; }

        /// <summary>Optional: the ward on mixed waves' ranged monsters (see WardDef). Absent: no ward.</summary>
        [JsonPropertyName("ward")]
        public WardDef Ward { get; set; }

        /// <summary>Optional: how mixed waves' corrupted monsters look and what they do (see CorruptedDef). Absent: defaults.</summary>
        [JsonPropertyName("corrupted")]
        public CorruptedDef Corrupted { get; set; }

        /// <summary>Optional: spawn portals around the ring; every wave comes out of them (see PortalsDef). Absent: no portals.</summary>
        [JsonPropertyName("portals")]
        public PortalsDef Portals { get; set; }

        /// <summary>The corrupted settings in force (the defaults when the goal sets none).</summary>
        [JsonIgnore]
        public CorruptedDef EffectiveCorrupted => Corrupted ?? (defaultCorrupted ??= new CorruptedDef());

        private CorruptedDef defaultCorrupted;

        /// <summary>The name the finale boss is given in front of its own ("Warlord"); blank keeps its name. ASCII only.</summary>
        [JsonPropertyName("finaleBossPrefix")]
        public string FinaleBossPrefix { get; set; } = DefaultFinaleBossPrefix;

        /// <summary>The effect played once when the finale boss arrives (a PlayScript name, or none). Default LevelUp.</summary>
        [JsonPropertyName("bossArrivalEffect")]
        public string BossArrivalEffect { get; set; } = DefaultBossArrivalEffect;

        /// <summary>Seconds the finale boss stands alone before the rest of the final wave arrives (0 to 30). Default 5.</summary>
        [JsonPropertyName("finaleBossLeadSeconds")]
        public double FinaleBossLeadSeconds { get; set; } = DefaultFinaleBossLeadSeconds;

        /// <summary>
        /// Optional: how many monsters each wave has (base / perParticipant / cap), replacing the source theme's
        /// waveCount for this goal. Absent keeps the theme's own size.
        /// </summary>
        [JsonPropertyName("waveSize")]
        public ScaledCount WaveSize { get; set; }

        /// <summary>
        /// Optional: the most wave monsters alive at once for this goal, replacing the theme's maxAlive (which is
        /// sized for one wave at a time). 0 or absent keeps the theme's.
        /// </summary>
        [JsonPropertyName("maxAlive")]
        public int MaxAlive { get; set; }

        /// <summary>
        /// Optional: the next wave arrives this many seconds after the last one, or sooner, see
        /// <see cref="NextWaveAliveShare"/>. Replaces the theme's waveIntervalSeconds for this goal. 0 or absent
        /// keeps the theme's cadence.
        /// </summary>
        [JsonPropertyName("waveIntervalSeconds")]
        public double WaveIntervalSeconds { get; set; }

        /// <summary>
        /// With <see cref="WaveIntervalSeconds"/>: the next wave also arrives early, as soon as no more than this
        /// share (0 to 1) of the last wave's size is still alive ("most of the wave is dead"). Default 0.25.
        /// </summary>
        [JsonPropertyName("nextWaveAliveShare")]
        public double NextWaveAliveShare { get; set; } = DefaultNextWaveAliveShare;

        /// <summary>
        /// With <see cref="WaveIntervalSeconds"/>: true makes the next wave wait for BOTH the interval and
        /// <see cref="NextWaveAliveShare"/> (whichever comes later), with <see cref="MaxWaveIntervalSeconds"/> as the
        /// ceiling. False (the default) sends it at whichever comes first.
        /// </summary>
        [JsonPropertyName("nextWaveRequiresBoth")]
        public bool NextWaveRequiresBoth { get; set; }

        /// <summary>With <see cref="NextWaveRequiresBoth"/>: the next wave comes after this long whatever is alive (0 to 600; 0 = no ceiling). Default 75.</summary>
        [JsonPropertyName("maxWaveIntervalSeconds")]
        public double MaxWaveIntervalSeconds { get; set; } = 75;

        /// <summary>With <see cref="WaveIntervalSeconds"/>: never two waves closer together than this (0 to 60 s). Default 8.</summary>
        [JsonPropertyName("minWaveGapSeconds")]
        public double MinWaveGapSeconds { get; set; } = DefaultMinWaveGapSeconds;

        /// <summary>
        /// Wave-based holds: at least this many special waves (breach, runners, ranged, hunters) per run, spread
        /// across the middle waves, whatever <see cref="SpecialWaveChance"/> rolls. Capped by the middle waves
        /// available. 0 (the default) leaves it to the chance.
        /// </summary>
        [JsonPropertyName("minSpecialWaves")]
        public int MinSpecialWaves { get; set; }

        /// <summary>
        /// The finale boss's size multiplier: 1 to 2.5, or 0 to make it as tall as the World Events named bosses
        /// (bosses.json: the median of their own height times their ObjScale times world_events_boss_scale_multiplier,
        /// read from the client DAT; WorldEventFinaleBoss.NamedBossHeight). Default 1.75; the envoys use 0.
        /// </summary>
        [JsonPropertyName("finaleBossScale")]
        public double FinaleBossScale { get; set; } = DefaultFinaleBossScale;

        /// <summary>
        /// The finale boss goes for the protected targets (WorldEventFinaleBoss): the healthiest first, named in a World
        /// Event line, a different one every <see cref="BossSwapSeconds"/> to <see cref="BossSwapMaxSeconds"/>, and
        /// players only while a taunt or a damage pull holds it, or no target stands. False (the default): it seeks the
        /// healthiest target like before, without the swap, the pull or the damage cap.
        /// </summary>
        [JsonPropertyName("bossTargetsProtected")]
        public bool BossTargetsProtected { get; set; }

        /// <summary>
        /// The least time, in seconds, the finale boss alone needs to take a protected target from full health to 0
        /// (20 to 300; 0 = no cap of its own). Its hits are sized to it and held to it by their own bucket, and also count
        /// against minDeathSeconds, so the boss and the wave together never beat that floor. Default 50.
        /// </summary>
        [JsonPropertyName("bossMinKillSeconds")]
        public double BossMinKillSeconds { get; set; } = DefaultBossMinKillSeconds;

        /// <summary>The finale boss swaps to a different target after a random time between this and bossSwapMaxSeconds (5 to 120). Default 15.</summary>
        [JsonPropertyName("bossSwapSeconds")]
        public double BossSwapSeconds { get; set; } = 15;

        /// <summary>The upper end of the swap time (at least bossSwapSeconds, up to 120). Default 20.</summary>
        [JsonPropertyName("bossSwapMaxSeconds")]
        public double BossSwapMaxSeconds { get; set; } = 20;

        /// <summary>
        /// Players pull the finale boss off a target by dealing this share of its max health within bossPullWindowSeconds
        /// (0.01 to 0.5; 0 = only a taunt pulls it). Default 0.06.
        /// </summary>
        [JsonPropertyName("bossPullShare")]
        public double BossPullShare { get; set; } = 0.06;

        /// <summary>The window the pull damage is counted over, in seconds (2 to 30). Default 8.</summary>
        [JsonPropertyName("bossPullWindowSeconds")]
        public double BossPullWindowSeconds { get; set; } = 8;

        /// <summary>
        /// How long a pull holds the boss on the player who dealt the most of that damage, in seconds (3 to 60); more
        /// damage in the window renews it. Default 10.
        /// </summary>
        [JsonPropertyName("bossPullSeconds")]
        public double BossPullSeconds { get; set; } = 10;

        /// <summary>
        /// The World Event line each time the boss turns on a target, one at random: tokens {boss} and {target}.
        /// Default "{boss} turns on {target}!".
        /// </summary>
        [JsonPropertyName("bossTargetAnnounce")]
        public List<string> BossTargetAnnounce { get; set; } = new List<string>();

        /// <summary>
        /// The local line when players pull the boss onto one of them: tokens {boss} and {player}. Default
        /// "{boss} turns on {player}!".
        /// </summary>
        [JsonPropertyName("bossPulledAnnounce")]
        public List<string> BossPulledAnnounce { get; set; } = new List<string>();

        public const double DefaultBossMinKillSeconds = 50;

        /// <summary>The targets look like they fight back (cosmetic only; WorldEventHoldDefense). Null: they stand at peace.</summary>
        [JsonPropertyName("defendPose")]
        public DefendPoseDef DefendPose { get; set; }

        /// <summary>
        /// Clear spawn area (WorldEventSpawnArea): before the scene is placed, the knight ring and each portal's path in
        /// are checked for buildings, trees, rocks and other objects. Null: no check.
        /// </summary>
        [JsonPropertyName("clearArea")]
        public ClearAreaDef ClearArea { get; set; }

        /// <summary>The finale boss's extra health multiplier (1 to 10), on top of its normal health. Default 3.</summary>
        [JsonPropertyName("finaleBossHealthMult")]
        public double FinaleBossHealthMult { get; set; } = DefaultFinaleBossHealthMult;

        /// <summary>
        /// The visible effect replayed on the finale boss (a PlayScript name, for example ShieldUpRed), or
        /// "none". Default ShieldUpRed.
        /// </summary>
        [JsonPropertyName("bossEffect")]
        public string BossEffect { get; set; } = DefaultBossEffect;

        /// <summary>How often the boss effect replays, in seconds (1 to 60). Default 3.</summary>
        [JsonPropertyName("bossEffectSeconds")]
        public double BossEffectSeconds { get; set; } = DefaultBossEffectSeconds;

        /// <summary>The most waves any wave-based hold can have, whatever protect.waves and the crowd say.</summary>
        public const int MaxHoldWaves = 10;

        /// <summary>
        /// Optional: wave-based hold. How many waves the group must survive (base / perParticipant / cap,
        /// sized at Active and raised, never lowered, as more players arrive, and never above
        /// <see cref="MaxHoldWaves"/>). When set, the hold succeeds the moment the LAST monster of the last
        /// (finale) wave dies with enough targets standing. No wave is ever added beyond the total. The final
        /// counted wave is a little bigger. Absent keeps the time-only hold (success at holdSeconds).
        /// holdSeconds stays the safety cap either way.
        /// </summary>
        [JsonPropertyName("waves")]
        public ScaledCount Waves { get; set; }

        /// <summary>
        /// Wave-based holds only: the pacing target. The waves are spread so the last one starts no earlier than
        /// this many seconds in (wave n of N waits until (n - 1) / (N - 1) of it has passed), so a group that
        /// clears fast waits a little between waves instead of being sent extra ones. It never delays success:
        /// the hold is won as soon as the finale is cleared.
        /// </summary>
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
        /// Optional, wave-based holds only: the special wave types this goal can roll (breach, runners, ranged,
        /// hunters) and its finale. Empty or absent means every wave is ordinary.
        /// </summary>
        [JsonPropertyName("specialWaves")]
        public List<SpecialWaveDef> SpecialWaves { get; set; }

        /// <summary>The chance (0 to 1) that a wave from <see cref="SpecialWaveFromWave"/> on is a special type. 0 (default) never.</summary>
        [JsonPropertyName("specialWaveChance")]
        public double SpecialWaveChance { get; set; }

        /// <summary>The first wave number that may be special. Default 2.</summary>
        [JsonPropertyName("specialWaveFromWave")]
        public int SpecialWaveFromWave { get; set; } = 2;

        /// <summary>
        /// Optional: for this long after a target is hit by a run monster, healing on it is reduced by
        /// <see cref="HealReductionAfterHit"/>, so heals buy time but cannot cancel an attacker on their own.
        /// </summary>
        [JsonPropertyName("healReductionSeconds")]
        public double HealReductionSeconds { get; set; } = 5;

        /// <summary>How much healing on a recently hit target is reduced (0 to 1). 0 (default) means no reduction.</summary>
        [JsonPropertyName("healReductionAfterHit")]
        public double HealReductionAfterHit { get; set; }

        /// <summary>
        /// Optional: target count and ring radius by crowd size, replacing <see cref="Count"/> and
        /// <see cref="Radius"/> (see TargetLayoutDef).
        /// </summary>
        [JsonPropertyName("countByParticipants")]
        public List<TargetLayoutDef> CountByParticipants { get; set; }

        /// <summary>
        /// The target count and ring radius for <paramref name="participants"/> players: the
        /// <see cref="CountByParticipants"/> entry with the highest minPlayers not above the crowd, else
        /// <see cref="ResolveCount"/> and <see cref="Radius"/>. The count stays within [1, MaxProtectedTargets].
        /// </summary>
        public (int Count, double Radius) ResolveLayout(int participants)
        {
            TargetLayoutDef best = null;

            if (CountByParticipants != null)
            {
                foreach (var entry in CountByParticipants)
                {
                    if (entry == null || entry.MinPlayers > participants)
                        continue;

                    if (best == null || entry.MinPlayers > best.MinPlayers)
                        best = entry;
                }
            }

            if (best == null)
                return (ResolveCount(participants), Radius);

            return (Math.Clamp(best.Count, 1, MaxProtectedTargets), best.Radius);
        }

        /// <summary>How far outside the widest target ring the waves spawn, at least (metres).</summary>
        public const double MinSpawnClearance = 10.0;

        /// <summary>The widest ring any crowd size can get: <see cref="Radius"/> or any countByParticipants radius.</summary>
        [JsonIgnore]
        public double MaxRingRadius
        {
            get
            {
                var max = double.IsFinite(Radius) ? Math.Max(0, Radius) : 0;

                if (CountByParticipants != null)
                    foreach (var entry in CountByParticipants)
                        if (entry != null && double.IsFinite(entry.Radius))
                            max = Math.Max(max, entry.Radius);

                return max;
            }
        }

        /// <summary>The finale entry of <see cref="SpecialWaves"/>, or null.</summary>
        [JsonIgnore]
        public SpecialWaveDef Finale => SpecialWaves?.Find(w => w != null && w.TypeKind == HoldWaveType.Finale);

        /// <summary>The <see cref="SpecialWaves"/> entry of <paramref name="type"/>, or null.</summary>
        public SpecialWaveDef SpecialWave(HoldWaveType type) => SpecialWaves?.Find(w => w != null && w.TypeKind == type);

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

        /// <summary>Said by a target restored at its shrine (protect.shrine).</summary>
        [JsonPropertyName("shrine")]
        public List<string> Shrine { get; set; } = new List<string>();

        /// <summary>Said by one surviving target when a corrupted monster dies and the targets heal (protect.corruptedKillHeal).</summary>
        [JsonPropertyName("corruptedKill")]
        public List<string> CorruptedKill { get; set; } = new List<string>();

        /// <summary>Every pool, with its goals.json key, for load-time repair and diagnostics.</summary>
        [JsonIgnore]
        public IEnumerable<(string Key, List<string> Pool)> Pools => new[]
        {
            ("spawn", Spawn), ("health", Health), ("destroyed", Destroyed), ("success", Success), ("heal", Heal),
            ("shrine", Shrine), ("corruptedKill", CorruptedKill)
        };
    }
}
