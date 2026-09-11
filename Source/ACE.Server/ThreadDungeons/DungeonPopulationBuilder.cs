using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The run-scoped tunables, read once by the caller (ThreadDungeonSpawner) and passed in, so nothing in
    /// the builder or the reward math reads PropertyManager. That is what makes both unit-testable: a
    /// PropertyManager read throws under the test harness.
    /// </summary>
    public readonly struct DungeonPopulationLimits
    {
        /// <summary>Hard cap on NON-BOSS creatures in one run (dynamic_dungeons_max_monsters_per_run).</summary>
        public readonly int MaxMonsters;
        public readonly double XpCap;
        public readonly double LumCap;
        public readonly int LootTierCap;
        public readonly double LootQualityCap;
        /// <summary>Ceiling on the product of the gem's lootQuantityMult values (dynamic_dungeons_loot_quantity_cap).</summary>
        public readonly double LootQuantityCap;

        /// <summary>
        /// How many times the toughest non-boss creature in the run the boss must be worth, at minimum
        /// (dynamic_dungeons_boss_health_floor_ratio). A FLOOR, never a multiplier stacked on the gem's own
        /// modifiers: the boss ends up at max(what its modifiers alone would give, this ratio times the
        /// observed pack maximum). Stacking instead would put a boss_guarded + hardy run near 24x base and
        /// risk a fight that cannot finish inside the run's TTL.
        /// </summary>
        public readonly double BossHealthFloorRatio;

        /// <summary>
        /// Multiple of the in-band trash pool's MEDIAN authored health (DungeonRosterSelector.BandMedianHealth)
        /// a non-boss creature's health must reach, applied BEFORE the gem's own health multiplier
        /// (dynamic_dungeons_trash_health_floor_ratio). Floor-then-multiply, the opposite order from
        /// <see cref="BossHealthFloorRatio"/>: the owner's ruling is "scaling up health to fit band standards
        /// BEFORE other multipliers are added", because this floor measures a BAND STANDARD (what a creature
        /// in this level range ought to carry, regardless of which family a run rolled) rather than a pack
        /// MAXIMUM observed after the fact. A FLOOR, never a multiplier stacked on the gem's own modifiers: a
        /// creature already at or above the floor is untouched.
        /// </summary>
        public readonly double TrashHealthFloorRatio;

        /// <summary>How far the boss's DamageRating must exceed the pack's (dynamic_dungeons_boss_damage_rating_floor).</summary>
        public readonly int BossDamageRatingFloor;

        /// <summary>How far the boss's DamageResistRating must exceed the pack's (dynamic_dungeons_boss_damage_resist_floor).</summary>
        public readonly int BossDamageResistFloor;

        /// <summary>
        /// Multiple of the GEM level the boss's stamped level must reach (dynamic_dungeons_boss_level_margin).
        /// One of four terms in the level floor; the pack maximum usually dominates it at the high rungs.
        /// </summary>
        public readonly double BossLevelMargin;

        /// <summary>
        /// The owner's headline rate (dynamic_dungeons_xp_scale): how many times what killing the same
        /// creature outside a dungeon pays a run kill is worth. Applied as its own term in
        /// <see cref="DungeonRewardMath.XpForKill"/>, deliberately OUTSIDE the XP modifier product and so
        /// outside <see cref="XpCap"/> - folding it in as a synthetic factor would let the cap clip it and
        /// make a plain gem and a four-modifier gem pay the same.
        /// </summary>
        public readonly double XpScale;

        /// <summary>
        /// The same rate for luminance (dynamic_dungeons_lum_scale), applied outside <see cref="LumCap"/> for
        /// the same reason.
        /// </summary>
        public readonly double LumScale;

        /// <summary>
        /// Master on/off for gem-level reward scaling (dynamic_dungeons_reward_scaling). False reproduces
        /// today's behaviour byte-for-byte: <see cref="DungeonRewardMath.RewardScaleRatio"/> is never
        /// consulted and every downstream axis this feature touches keeps its raw, unscaled value.
        /// </summary>
        public readonly bool RewardScalingEnabled;

        /// <summary>
        /// The gem level at which the reward-scale ratio is exactly 1.0 (dynamic_dungeons_reward_scale_anchor).
        /// Deliberately NOT pinned to <see cref="DungeonGemSpec.MaxLevel"/>: it was set to 300 while the
        /// ceiling was 275, so that a ceiling raise would not silently devalue every existing gem.
        ///
        /// THE 2026-09-09 RAISE TO 375 PUT THE CEILING ABOVE THE ANCHOR, and the anchor was deliberately left
        /// where it is. The consequence is not a devaluation - it is the opposite: dynamic_dungeons_reward_
        /// scale_cap defaults to 0, which means UNCAPPED, so gems above 300 now scale past 1.0 rather than
        /// stopping there. That is the behaviour the missing ceiling was written for. Whether 300 is still
        /// the right anchor for a 375 ceiling is a TUNING question, not a correctness one, and it is the
        /// repo owner's call - it is a live tunable either way.
        /// </summary>
        public readonly double RewardScaleAnchor;

        /// <summary>The k of (level / anchor)^k (dynamic_dungeons_reward_scale_exponent).</summary>
        public readonly double RewardScaleExponent;

        /// <summary>Lower bound on the reward-scale ratio (dynamic_dungeons_reward_scale_floor).</summary>
        public readonly double RewardScaleFloor;

        /// <summary>
        /// Upper bound on the reward-scale ratio (dynamic_dungeons_reward_scale_cap). Zero or less means
        /// uncapped - deliberate, so growth above the anchor is unbounded until an admin sets an explicit
        /// safety valve.
        /// </summary>
        public readonly double RewardScaleCap;

        /// <summary>
        /// The roster band's low edge multiplier (dynamic_dungeons_roster_band_low), carried through to
        /// DungeonRosterSelector as a <see cref="DungeonRosterBand"/> rather than read there directly, on the
        /// same "the builder is pure, PropertyManager throws under the test harness" contract as every other
        /// dial on this struct.
        /// </summary>
        public readonly double RosterBandLow;

        /// <summary>
        /// The roster band's high edge multiplier (dynamic_dungeons_roster_band_high). Narrowed from 1.5 to
        /// 1.15 (owner ruling, 2026-09-08) - see <see cref="DungeonRosterSelector.BandHigh"/> for why.
        /// </summary>
        public readonly double RosterBandHigh;

        /// <summary>
        /// How far the roster band's LOW edge may step down when the natural band's pool is too thin
        /// (dynamic_dungeons_band_low_floor), as a fraction of the gem level. 1.0 disables the adaptive band
        /// and the uplift that goes with it - see <see cref="DungeonRosterSelector.BandLowFloor"/> for the
        /// measured pool-collapse this exists to fix, and for why the HIGH edge never moves.
        /// </summary>
        public readonly double BandLowFloorRatio;

        /// <summary>
        /// How many species tables must field an in-band trash member before the band stops widening
        /// (dynamic_dungeons_min_eligible_families). 0 or less disables family widening.
        /// </summary>
        public readonly int MinEligibleFamilies;

        /// <summary>
        /// How many DISTINCT trash wcids the chosen family's pool must hold before its own band stops
        /// widening (dynamic_dungeons_min_family_pool). 0 or less disables per-family widening.
        /// </summary>
        public readonly int MinFamilyPool;

        /// <summary>
        /// Whether an uplifted creature's name is prefixed (dynamic_dungeons_uplift_rename). A normalized
        /// creature is not the monster its weenie describes - its level, skills, melee damage and body armour
        /// have all been raised to the band standard - and the examine panel is the only place a player can
        /// see that before the fight. False leaves every name untouched, which is what
        /// <see cref="BandLowFloorRatio"/> = 1.0 also produces (there is nothing to rename when nothing is
        /// uplifted).
        /// </summary>
        public readonly bool UpliftRename;

        /// <summary>The compiled defaults, shared with the PropertyManager registrations so the two cannot drift.</summary>
        public const double DefaultBossHealthFloorRatio = 4.0;

        /// <summary>The compiled default for the trash health floor ratio (owner ruling 2026-09-07).</summary>
        public const double DefaultTrashHealthFloorRatio = 0.5;
        public const int DefaultBossDamageRatingFloor = 30;
        public const int DefaultBossDamageResistFloor = 15;
        public const double DefaultBossLevelMargin = 1.10;
        public const double DefaultXpScale = 2.0;
        public const double DefaultLumScale = 2.0;

        /// <summary>
        /// The compiled defaults for the roster band edges, referencing DungeonRosterSelector's own constants
        /// so the two cannot drift - the same shared-constant contract as the block above.
        /// </summary>
        public const double DefaultRosterBandLow = DungeonRosterSelector.BandLow;
        public const double DefaultRosterBandHigh = DungeonRosterSelector.BandHigh;

        /// <summary>
        /// The compiled defaults for the adaptive band and its uplift, again referencing
        /// DungeonRosterSelector's own constants so the PropertyManager registration, this struct and the
        /// selection code are provably one value each.
        /// </summary>
        public const double DefaultBandLowFloorRatio = DungeonRosterSelector.BandLowFloor;
        public const int DefaultMinEligibleFamilies = DungeonRosterSelector.MinEligibleFamilies;
        public const int DefaultMinFamilyPool = DungeonRosterSelector.MinFamilyPool;
        public const bool DefaultUpliftRename = true;

        /// <summary>
        /// The prefix an uplifted creature's name carries when <see cref="UpliftRename"/> is on. ASCII only,
        /// like every other player-facing string in this subsystem. Applied INSIDE the role prefix
        /// (ThreadDungeonSpawner writes "Elite Threadbound X", never "Threadbound Elite X"), because the role
        /// word is what a player scans a room for and it stays leftmost.
        /// </summary>
        public const string UpliftNamePrefix = "Threadbound ";

        /// <summary>The compiled defaults for the gem-level reward scaling curve, same shared-constant contract.</summary>
        public const bool DefaultRewardScalingEnabled = true;
        public const double DefaultRewardScaleAnchor = 300.0;
        public const double DefaultRewardScaleExponent = 2.87;
        public const double DefaultRewardScaleFloor = 0.10;
        public const double DefaultRewardScaleCap = 0.0;

        /// <summary>
        /// The compiled defaults for the three reward-product CEILINGS, on the same shared-with-PropertyManager
        /// contract as the block above. They exist as constants because the gem's own description has to quote
        /// the same capped products the run will actually pay (ThreadDungeonGemHandler.ComposeLongDesc), and
        /// that function's pure core must not read PropertyManager - a read throws under the unit-test harness.
        /// It therefore takes the three caps as optional parameters defaulted to these, and only the
        /// store-bound overload resolves the live tunables.
        /// </summary>
        public const double DefaultXpCap = 3.0;
        public const double DefaultLumCap = 3.0;
        public const double DefaultLootQuantityCap = 3.0;

        /// <summary>
        /// Typo ceilings for the two double dials. These are NOT a statement about what a good ratio or
        /// margin is - the shipped values are 4.0 and 1.10, and anything up to these is a legitimate tuning
        /// choice the ceiling deliberately does not judge. They exist because both dials are LIVE, editable
        /// with one /pm command and with no content-lint gate in front of them, and because a fat-fingered
        /// or copy-pasted value fails in the worst possible direction: BossHealthTarget saturates its RESULT
        /// at int.MaxValue rather than rejecting an unreasonable INPUT, so a ratio of 100000 would pin every
        /// subsequently opened dungeon's boss near two billion health and make the run unfinishable, with
        /// nothing but a log line to explain it.
        ///
        /// 50x the pack's toughest member, and 5x the gem level, are both far past any plausible tuning and
        /// still comfortably short of "unkillable". Raise them if a real design ever wants more; do not
        /// remove them.
        /// </summary>
        public const double MaxBossHealthFloorRatio = 50.0;
        public const double MaxBossLevelMargin = 5.0;

        /// <summary>
        /// Typo ceiling for the trash health floor ratio, on the same reasoning as the boss ceilings above:
        /// LIVE, editable with one /pm command, and sitting in front of every non-boss creature's health. The
        /// shipped value is 0.5 (half the band's median); 5x the band median is far past any plausible tuning
        /// and still short of turning every trash creature into a de facto boss. Raise it if a real design
        /// ever wants more; do not remove it.
        /// </summary>
        public const double MaxTrashHealthFloorRatio = 5.0;

        /// <summary>
        /// Typo ceiling for the two reward scalars, on the same reasoning as the two boss ceilings above:
        /// both are LIVE, editable with one /pm command, and sit in front of every XP and luminance award a
        /// run pays. 20x retail is far past any plausible tuning of a 2.0x headline rate and still short of
        /// an amount that would trivialise levelling in one run.
        /// </summary>
        public const double MaxRewardScale = 20.0;

        /// <summary>
        /// The four boss-uplift terms and the two reward scalars are OPTIONAL-DEFAULTED so every existing
        /// call site keeps compiling and so a test that does not care about them still gets the shipped
        /// behaviour rather than zero. Sanitising is the caller's job (ThreadDungeonSpawner.TryPopulate),
        /// because only the caller knows a tunable was involved; the values that arrive here are already clean.
        /// </summary>
        public DungeonPopulationLimits(int maxMonsters, double xpCap, double lumCap, int lootTierCap, double lootQualityCap,
            double lootQuantityCap = 1.0,
            double bossHealthFloorRatio = DefaultBossHealthFloorRatio,
            int bossDamageRatingFloor = DefaultBossDamageRatingFloor,
            int bossDamageResistFloor = DefaultBossDamageResistFloor,
            double bossLevelMargin = DefaultBossLevelMargin,
            double xpScale = DefaultXpScale,
            double lumScale = DefaultLumScale,
            bool rewardScalingEnabled = DefaultRewardScalingEnabled,
            double rewardScaleAnchor = DefaultRewardScaleAnchor,
            double rewardScaleExponent = DefaultRewardScaleExponent,
            double rewardScaleFloor = DefaultRewardScaleFloor,
            double rewardScaleCap = DefaultRewardScaleCap,
            double trashHealthFloorRatio = DefaultTrashHealthFloorRatio,
            double rosterBandLow = DefaultRosterBandLow,
            double rosterBandHigh = DefaultRosterBandHigh,
            double bandLowFloorRatio = DefaultBandLowFloorRatio,
            int minEligibleFamilies = DefaultMinEligibleFamilies,
            int minFamilyPool = DefaultMinFamilyPool,
            bool upliftRename = DefaultUpliftRename)
        {
            MaxMonsters = maxMonsters;
            XpCap = xpCap;
            LumCap = lumCap;
            LootTierCap = lootTierCap;
            LootQualityCap = lootQualityCap;
            LootQuantityCap = lootQuantityCap;
            BossHealthFloorRatio = bossHealthFloorRatio;
            BossDamageRatingFloor = bossDamageRatingFloor;
            BossDamageResistFloor = bossDamageResistFloor;
            BossLevelMargin = bossLevelMargin;
            XpScale = xpScale;
            LumScale = lumScale;
            RewardScalingEnabled = rewardScalingEnabled;
            RewardScaleAnchor = rewardScaleAnchor;
            RewardScaleExponent = rewardScaleExponent;
            RewardScaleFloor = rewardScaleFloor;
            RewardScaleCap = rewardScaleCap;
            TrashHealthFloorRatio = trashHealthFloorRatio;
            RosterBandLow = rosterBandLow;
            RosterBandHigh = rosterBandHigh;
            BandLowFloorRatio = bandLowFloorRatio;
            MinEligibleFamilies = minEligibleFamilies;
            MinFamilyPool = minFamilyPool;
            UpliftRename = upliftRename;
        }
    }

    public sealed class DungeonSpawnPlanEntry
    {
        public uint Wcid { get; }
        public DungeonRole Role { get; }
        public DungeonSpawnPointDef Point { get; }

        /// <summary>
        /// The level this entry must be normalized UP to, or 0 for "no uplift" - which is every entry a run
        /// drew from its natural band, and every entry of every run while
        /// DungeonPopulationLimits.BandLowFloorRatio is 1.0.
        ///
        /// Set to the GEM's level (never the creature's own, never the band's edge) for a pick whose live
        /// weenie level falls BELOW the natural band's low edge - i.e. one that could only have been drawn
        /// because the band widened downward. The gem level is what the player was promised and what the rest
        /// of the run is priced against, so it is the one number the extension's creatures have to be brought
        /// to; using the band edge instead would leave them a rounding step under everything else in the room.
        ///
        /// A zero here is a hard contract, not a default that happens to be harmless: ThreadDungeonSpawner
        /// runs NO part of the normalization block for a zero entry, so an unwidened run is byte-for-byte the
        /// run it was before this feature existed (invariants I-A, I-D and I-E).
        /// </summary>
        public int UpliftLevel { get; }

        /// <summary>
        /// <paramref name="upliftLevel"/> is optional-defaulted to 0 so every pre-existing construction site
        /// and test keeps compiling and keeps meaning "not uplifted", which is what they all were.
        /// </summary>
        public DungeonSpawnPlanEntry(uint wcid, DungeonRole role, DungeonSpawnPointDef point, int upliftLevel = 0)
        {
            Wcid = wcid;
            Role = role;
            Point = point;
            UpliftLevel = upliftLevel;
        }
    }

    /// <summary>
    /// One run's complete population, decided before a single object exists. <see cref="Entries"/> holds EVERY
    /// creature the run will place, boss included - the boss is an entry with <see cref="DungeonRole.Boss"/>,
    /// not a field alongside them, because ThreadDungeonRun's clear rule counts the boss into both Planned
    /// and Spawned, and derives trash counts (TrashSpawned/TrashKilled) by subtracting the boss slot back out
    /// (owner ruling R28: weighted progress = BossWeight*(boss dead) + (1-BossWeight)*trashFraction, or just
    /// trashFraction when no boss is present).
    /// </summary>
    public sealed class DungeonSpawnPlan
    {
        public List<DungeonSpawnPlanEntry> Entries { get; } = new List<DungeonSpawnPlanEntry>();
        public uint BossWcid { get; set; }
        public string FamilyId { get; set; }
        public double HealthMultiplier { get; set; } = 1.0;
        public double BossHealthMultiplier { get; set; } = 1.0;

        /// <summary>
        /// The band-median health floor (DungeonRosterSelector.BandMedianHealth times
        /// DungeonPopulationLimits.TrashHealthFloorRatio), computed ONCE per run against the gem's own level
        /// and applied to every non-boss entry, elites included, BEFORE <see cref="HealthMultiplier"/> - the
        /// opposite order from the boss floor, which measures the pack's OBSERVED maximum AFTER multiplying.
        /// 0 when the axis is disabled (ratio 0) or the cross-family trash pool has no usable health data,
        /// and 0 is a no-op at the spawner (Math.Max leaves an untouched creature untouched).
        /// </summary>
        public uint TrashHealthFloor { get; set; }

        /// <summary>
        /// What a creature drawn at this gem's NATURAL band carries, per axis, and so what every entry with
        /// a nonzero <see cref="DungeonSpawnPlanEntry.UpliftLevel"/> is normalized up to at placement time.
        /// <see cref="DungeonBandStandard.Empty"/> - the no-op - whenever no entry was uplifted, when the
        /// caller supplied no profile delegate, or when the band's sample held no usable data.
        ///
        /// Computed here rather than at the spawner for the same reason everything else on this plan is: the
        /// builder is pure and the whole plan can be logged and unit-tested before a single object exists.
        /// </summary>
        public DungeonBandStandard BandStandard { get; set; } = DungeonBandStandard.Empty;

        /// <summary>
        /// Whether an uplifted creature gets DungeonPopulationLimits.UpliftNamePrefix, carried from the
        /// tunables so the spawner does not read PropertyManager on the placement path.
        /// </summary>
        public bool UpliftRename { get; set; } = DungeonPopulationLimits.DefaultUpliftRename;

        /// <summary>
        /// The NATURAL band's integer low edge, floor(gemLevel * RosterBandLow) - the threshold a pick's live
        /// level had to fall below to be tagged for uplift. Carried for the log line and for telemetry;
        /// nothing derives behaviour from it (the tag is already on the entry).
        /// </summary>
        public int NaturalBandLow { get; set; }

        /// <summary>
        /// The integer low edge the run's trash pool was actually drawn from, after any widening. Equal to
        /// <see cref="NaturalBandLow"/> when nothing widened. The two together are what makes a widened run
        /// legible in the log: "drew [165, 317] against a natural [275, 317]".
        /// </summary>
        public int EffectiveBandLow { get; set; }

        public int DamageRating { get; set; }
        public int CritRating { get; set; }
        public int CritDamageRating { get; set; }
        public int DamageResistRating { get; set; }
        public int BossDamageRating { get; set; }

        /// <summary>
        /// Invariant I4. Already floored: max(what the boss's own modifiers give, the pack's
        /// DamageResistRating + BossDamageResistFloor). The spawner uses this in place of
        /// <see cref="DamageResistRating"/> for the boss and for nobody else.
        /// </summary>
        public int BossDamageResistRating { get; set; }

        /// <summary>
        /// Invariant I1, the level the spawner STAMPS on the boss - not necessarily the boss weenie's own
        /// level. Strictly above every non-boss entry's level and above the gem's level, and never below the
        /// boss weenie's authored level. 0 when the plan has no boss.
        /// </summary>
        public int BossLevel { get; set; }

        /// <summary>
        /// Invariant I2's ratio, carried from the tunables so the spawner does not read PropertyManager on
        /// the placement path. The floor is applied against OBSERVED health at placement time, not here: a
        /// creature's real Health.MaxValue folds a dat-driven attribute formula (AttributeFormula.cs:37-38)
        /// that a pure builder cannot evaluate.
        /// </summary>
        public double BossHealthFloorRatio { get; set; } = DungeonPopulationLimits.DefaultBossHealthFloorRatio;

        public double RunSpeedMult { get; set; } = 1.0;

        /// <summary>
        /// Fraction of the defender's shield every run creature ignores, from
        /// <see cref="DungeonRewardMath.IgnoreShieldFraction"/>. 0 leaves the axis untouched.
        ///
        /// ONE field for the whole run rather than a boss/non-boss pair, matching CritRating and
        /// DamageResistRating's own precedent: the shipped shield_hollow row is target "monster", so it
        /// already applies to every creature including the boss, and no bosses.json row names it. Splitting
        /// it would add a second field nothing writes differently.
        /// </summary>
        public double IgnoreShield { get; set; }

        /// <summary>
        /// Whether every run creature ignores magic armor and magic resistance outright, from
        /// <see cref="DungeonRewardMath.IsHollow"/>. Binary - see DungeonRewardMath.Hollow for why there is
        /// no partial form - and one field for the whole run, on the same reasoning as
        /// <see cref="IgnoreShield"/>.
        /// </summary>
        public bool Hollow { get; set; }
        public double XpMultiplier { get; set; } = 1.0;
        public double BossXpMultiplier { get; set; } = 1.0;
        public double LumMultiplier { get; set; } = 1.0;

        /// <summary>
        /// The owner's headline rate, carried from the tunables. Applied OUTSIDE the modifier product and so
        /// outside its cap, which is the whole point of it being a separate field rather than a synthetic
        /// modifier row: a plain gem pays this much more than retail, and a four-modifier gem still pays
        /// its modifier product on top.
        /// </summary>
        public double XpScale { get; set; } = DungeonPopulationLimits.DefaultXpScale;

        /// <summary>The same rate for luminance.</summary>
        public double LumScale { get; set; } = DungeonPopulationLimits.DefaultLumScale;
        public TreasureDeath Profile { get; set; }

        /// <summary>
        /// The run's loot-quantity multiplier, AFTER the gem's modifier product has been clamped to
        /// DungeonPopulationLimits.LootQuantityCap and the gem-level reward-scale ratio applied - i.e. the
        /// exact scalar that was handed to <see cref="DungeonRewardMath.BuildProfile"/>. 1.0 is the neutral
        /// value and leaves every consumer untouched.
        ///
        /// It is carried rather than recomputed because the boss cache (ThreadDungeonRewardSpawner) rolls a
        /// FIXED number of items rather than one profile draw, so it needs the scalar and not just the
        /// profile's already-scaled maximums.
        /// </summary>
        public double LootQuantityMult { get; set; } = 1.0;

        /// <summary>
        /// The gem's salvage affinities, as (material, base wcid, per-kill probability). Stamped onto every
        /// run creature before EnterWorld and read at its death; empty for a gem carrying none, which is
        /// every gem shipped before this feature.
        /// </summary>
        public IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> SalvageAffinities { get; set; }
            = new List<(int, uint, double)>();

        public List<string> Notes { get; } = new List<string>();

        /// <summary>
        /// A boss was WANTED and none could be fielded: the dungeon has a boss anchor, the curated
        /// bosses.json window came back empty, and the run's own family either does not exist or could
        /// promote nobody. False when the dungeon simply has no boss anchor, because then no boss was ever
        /// asked for and there is no failure to report.
        ///
        /// A structured flag rather than a note, deliberately. <see cref="Notes"/> is free text for a human
        /// reading the log, its wording is not a contract, and telemetry that pattern-matched it would break
        /// the first time somebody reworded a message. The spawner reads this flag to emit the
        /// no_candidate placement code and leaves the notes to the log.
        /// </summary>
        public bool BossNoCandidate { get; set; }
    }

    /// <summary>
    /// Turns a gem + dungeon + store into a concrete spawn plan, pure (TECH-DESIGN 3.6). Everything the
    /// spawner writes onto a creature is decided here so the plan can be unit-tested and logged before a
    /// single object exists.
    ///
    /// Nothing here touches the world, the database or PropertyManager, and the only source of randomness is
    /// the <c>rng</c> the caller hands in - the spawner seeds it from the gem, so the same gem always draws
    /// the same dungeon.
    ///
    /// An unbuildable population is NOT an error. A gem whose level band matches no species table, or a
    /// dungeon whose boss window is empty, yields a plan with fewer entries and a Note; the run still opens
    /// and is still exitable (PLAN 6). The spawner logs every note at WARN.
    /// </summary>
    public static class DungeonPopulationBuilder
    {
        public const double DefaultEliteShare = 0.15;

        /// <summary>
        /// The bosses.json level window, as a multiple of the GEM level. FLAVOUR ONLY since 2026-09-06: it
        /// decides which curated rows are allowed to headline a run, and nothing else. The guarantee that a
        /// boss outranks its own pack is carried by the uplift computed below (invariants I1 to I4) and
        /// applied to whoever holds the Boss role, curated or promoted.
        ///
        /// The low edge was raised from 0.8x to 1.0x with that change. At 0.8x the window overlapped the
        /// trash band ([1.0x, 1.5x]) from underneath, so a level-185 gem could pick a level-148 curated boss
        /// to headline a pack drawing up to level 278 - the uplift would have rescued it, but starting a
        /// boss below every creature in the room is not a draw worth making in the first place.
        /// </summary>
        public const double BossLevelWindowLow = 1.0;
        public const double BossLevelWindowHigh = 1.6;

        /// <summary>
        /// What a PROMOTED boss is treated as carrying, on top of whatever the gem itself rolled. Fed through
        /// the same DungeonRewardMath extra-modifier path a curated bosses.json row's own Modifiers list uses,
        /// at each modifier's MinMagnitude, so promotion invents no second vocabulary and a promoted boss and
        /// a curated one are priced by exactly the same arithmetic.
        /// </summary>
        public static readonly string[] PromotedBossModifiers = { "boss_guarded" };

        /// <summary>
        /// The fork's luminance standard (adopted 2026-08-17, Source/.claude/skills/weenie-generator/
        /// references/monster_patterns.md "The luminance standard"): a hostile at level 185 or above carries
        /// LuminanceAward = XpOverride / 4,000 rounded to the nearest 50, and below 185 carries none. A run
        /// creature is priced by that same rule off its own retail XP, so a dungeon kill and an outdoor kill
        /// of the same creature sit on one curve.
        /// </summary>
        public const int LuminanceBaseLevel = 185;

        /// <summary>Retail XP per point of luminance, from the standard.</summary>
        public const int LuminanceXpPerLum = 4000;

        /// <summary>The grid the standard's quotient is snapped to, ONCE, before role and scale are applied.</summary>
        public const int LuminanceRounding = 50;

        /// <param name="profileOf">
        /// The one delegate the band-standard uplift reads (see <see cref="DungeonStatProfile"/>). Optional
        /// and trailing so every call site that predates the uplift keeps compiling; null means "no standard
        /// is available", under which entries are still TAGGED for uplift but
        /// <see cref="DungeonSpawnPlan.BandStandard"/> stays empty and the spawner normalizes nothing but the
        /// level. It is only ever consulted when at least one entry was actually tagged, so a run whose band
        /// never widened pays nothing for it.
        /// </param>
        /// <param name="fitsDungeon">
        /// The physical fit predicate for THIS dungeon (see <see cref="DungeonFitFilter"/>). Optional and
        /// trailing, exactly as <paramref name="profileOf"/> was added, so every call site that predates the
        /// fit filter keeps compiling; NULL MEANS NO CONSTRAINT and is byte-identical to the pre-filter
        /// behaviour - no projection is built and the raw species dictionary is used throughout.
        ///
        /// It is applied at ROSTER CONSTRUCTION, never at placement, and never to the pools after they are
        /// drawn. Both of those alternatives were considered and rejected:
        ///   - filtering at PLACEMENT leaves the spawn point empty, which shrinks the run's trash count, and a
        ///     refused boss leaves bossPlaced false, permanently withholding dynamic_dungeons_boss_clear_weight
        ///     of the run's progress and capping it below ClearFraction forever (see the Spawned accounting in
        ///     ThreadDungeonSpawner);
        ///   - filtering the POOLS post-hoc defeats the band widening, because
        ///     DungeonRosterSelector.ResolveFamilyEliteBand stops at the first band whose pool is non-empty and
        ///     ResolveFamilyTrashBand at the first reaching MinFamilyPool, both evaluated PRE-filter - so the
        ///     widening accepts a band that is empty post-filter and hands back nothing.
        /// Filtering the roster the selector SEES makes the widening search the filtered pools, which is the
        /// only ordering under which "widen until there are enough" and "only count who fits" agree.
        /// </param>
        public static DungeonSpawnPlan Build(DungeonGemSpec spec, DungeonEntryDef dungeon, ThreadDungeonStore store,
            IReadOnlyDictionary<string, SpeciesTableDef> species, Func<uint, int> levelOf, Func<uint, uint> healthOf,
            DungeonPopulationLimits limits, Random rng, Func<uint, DungeonStatProfile> profileOf = null,
            Func<uint, bool> fitsDungeon = null)
        {
            var plan = new DungeonSpawnPlan();
            var mods = store.Modifiers;

            // Resolved ONCE and forwarded to every DungeonRosterSelector call below - the health floor's band
            // and the roster's own selection band must provably be the same number (see the comment on the
            // health floor immediately below), and a call site that silently fell back to
            // DungeonRosterBand.Default instead of forwarding this would draw from the WRONG band the moment
            // an admin edited either tunable away from its compiled default.
            var band = new DungeonRosterBand(limits.RosterBandLow, limits.RosterBandHigh);

            // The adaptive band's floor. Sanitizing a garbled dial is the READER's job
            // (ThreadDungeonSpawner.ReadBandFloorDial), exactly as it is for TrashHealthFloorRatio and the
            // boss dials; what reaches a limits object built directly in code is treated as DISABLED rather
            // than propagated, and 1.0 is the disabled value - the low edge cannot then move at all, because
            // LowEdgeLadder yields only the natural low when the floor is at or above it.
            //
            // A value ABOVE 1.0 also reads as 1.0 rather than as an error: a floor above the natural low edge
            // could only describe widening UPWARD, and the high edge never moves.
            var lowFloor = double.IsNaN(limits.BandLowFloorRatio) || double.IsInfinity(limits.BandLowFloorRatio)
                || limits.BandLowFloorRatio <= 0 || limits.BandLowFloorRatio > 1.0
                ? 1.0
                : limits.BandLowFloorRatio;

            // ---- physical fit projection (DungeonFitFilter) ----
            // Done ONCE, here, before anything reads the roster: every eligibility test, every band-widening
            // search and every draw below runs against speciesForSelection, so "widen the band until enough
            // creatures qualify" and "only count creatures that fit this dungeon" can never disagree.
            //
            // speciesForSelection is the ONLY thing that changes. The RAW dictionary is still what the
            // band-statistics calls read - see the comments at BandMedianHealth and DungeonBandStandard.Compute
            // below for why that is deliberate and not an oversight.
            var speciesForSelection = species;

            if (fitsDungeon != null)
            {
                var projection = DungeonFitFilter.Project(species, fitsDungeon);

                // How many families could this dictionary field for THIS gem, after every widening step the
                // floor allows? Evaluated at each dictionary's own widest reachable band rather than at the
                // natural one, because the widening is exactly what a thin post-filter roster relies on: a
                // family with nothing in the natural band may still be perfectly playable two rungs down.
                //
                // A gem that FORCES a family collapses the question to that one table, because PickFamily
                // refuses a forced family it cannot satisfy rather than substituting - so a projection that
                // leaves other families eligible but empties the forced one would still open the run bare.
                // That is the /dd give path, and it is the one this arm exists for.
                int EligibleUnderProjection(IReadOnlyDictionary<string, SpeciesTableDef> tables)
                {
                    var resolved = DungeonRosterSelector.ResolveEligibilityBand(tables, spec.Level, levelOf, band,
                        lowFloor, limits.MinEligibleFamilies);

                    if (!string.IsNullOrEmpty(spec.Family) && spec.Family != DungeonGemSpec.Any)
                    {
                        return tables.TryGetValue(spec.Family, out var forced) && forced != null
                            && DungeonRosterSelector.IsEligible(forced, spec.Level, levelOf, resolved) ? 1 : 0;
                    }

                    return DungeonRosterSelector.EligibleFamilyCount(tables, spec.Level, levelOf, resolved);
                }

                // FAIL-OPEN GUARD. A run that opens with no trash and forms no boss is a worse outcome than
                // the doorway exploit this filter exists to close: the player is left in an empty copy, and a
                // boss that never spawns permanently withholds dynamic_dungeons_boss_clear_weight of the run's
                // progress. So when the projection would leave NOTHING eligible where the raw roster had
                // something, the projection is discarded outright and the run is built exactly as it would
                // have been before this feature existed.
                //
                // Only reached when the projection actually emptied the field - the raw count is not computed
                // otherwise, so the ordinary case pays one eligibility sweep, not two.
                if (EligibleUnderProjection(projection.Species) == 0 && EligibleUnderProjection(species) > 0)
                {
                    plan.Notes.Add($"fit: excluding {projection.RemovedWcids} creature(s) too tall for this dungeon would leave no eligible " +
                                   $"family for level {spec.Level}; fit filter bypassed for this run");
                }
                else
                {
                    speciesForSelection = projection.Species;

                    if (projection.RemovedWcids > 0)
                        plan.Notes.Add($"fit: {projection.RemovedWcids} creature(s) excluded as too tall for this dungeon" +
                                       (projection.EmptiedFamilies > 0 ? $"; {projection.EmptiedFamilies} family/families left with no members" : ""));
                }
            }

            plan.UpliftRename = limits.UpliftRename;

            // The uplift threshold, fixed against the NATURAL band before any widening: a pick below this
            // could only have been reached by the extension.
            plan.NaturalBandLow = DungeonRosterSelector.NaturalLowEdge(spec.Level, band);
            plan.EffectiveBandLow = plan.NaturalBandLow;

            plan.HealthMultiplier = DungeonRewardMath.HealthMultiplier(spec, mods, Array.Empty<string>());

            // The non-boss health floor. Computed ONCE against the gem's own level, never against the drawn
            // family, which would collapse this back to the family-scoped median the whole point of
            // BandMedianHealth is to avoid.
            //
            // Deliberately against the NATURAL band, not the widened draw band the selection below may end
            // up using. Health is the one axis the uplift does NOT re-do (see DungeonBandStandard's class
            // comment), so this floor IS the uplift's health term - and a floor computed over the widened
            // band would be the median of a pool that includes the very under-levelled creatures the uplift
            // exists to raise, which is a floor that lowers itself the more it is needed.
            var trashRatio = double.IsNaN(limits.TrashHealthFloorRatio) || limits.TrashHealthFloorRatio < 0
                ? 0.0
                : limits.TrashHealthFloorRatio;
            //
            // THE RAW DICTIONARY, NOT speciesForSelection, AND THAT IS DELIBERATE. This is a statistic about
            // the BAND - what a level-N creature in this band typically carries - not about who may be placed
            // in this particular dungeon. Filtering it would make the same gem log two different health floors
            // depending on which dungeon it opened, and would move the floor by removing the seven tallest
            // (and generally heaviest) creatures from the sample.
            var bandMedian = trashRatio > 0 ? DungeonRosterSelector.BandMedianHealth(species, spec.Level, levelOf, healthOf, band) : 0;
            plan.TrashHealthFloor = (uint)Math.Clamp(Math.Round(bandMedian * trashRatio), 0, int.MaxValue);
            plan.DamageRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.DamageRating);
            plan.CritRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.CritRating);
            plan.CritDamageRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.CritDamageRating);
            plan.DamageResistRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.DamageResistRating);
            plan.RunSpeedMult = DungeonRewardMath.RunValue(spec, mods, DungeonRewardMath.RunSpeedMult, 1.0);
            plan.IgnoreShield = DungeonRewardMath.IgnoreShieldFraction(spec, mods, Array.Empty<string>());
            plan.Hollow = DungeonRewardMath.IsHollow(spec, mods, Array.Empty<string>());
            plan.XpMultiplier = DungeonRewardMath.XpMultiplier(spec, mods, limits.XpCap);
            plan.BossXpMultiplier = DungeonRewardMath.BossXpMultiplier(spec, mods, limits.XpCap);
            plan.LumMultiplier = DungeonRewardMath.LumMultiplier(spec, mods, limits.LumCap);

            // The gem-level reward-scale ratio (owner requirement, 2026-09-07): every BENEFICIAL multiplier
            // a run grants scales with the gem's level, applied ONCE here so every downstream consumer of the
            // plan already sees the scaled numbers. Deliberately left at 1.0 - a no-op - when the master
            // switch is off, so that path reproduces today's behaviour exactly. Left OUT of XpMultiplier/
            // BossXpMultiplier/LumMultiplier above: those are the gem's own ROLLED modifier products, already
            // gem-specific, and are not the "uniform server-wide scalar" this feature targets.
            var rewardScaleRatio = limits.RewardScalingEnabled
                ? DungeonRewardMath.RewardScaleRatio(spec.Level, limits.RewardScaleAnchor, limits.RewardScaleExponent,
                    limits.RewardScaleFloor, limits.RewardScaleCap)
                : 1.0;

            // Carried, never folded into the products above: the caps clip those, and the scalars must sit
            // outside them or a plain gem and a four-modifier gem would pay identically.
            //
            // MULTIPLICATIVE axis (neutral value 1.0): effective = 1 + ratio * (raw - 1). Never ratio * raw -
            // at a low ratio that would pay LESS than retail, which inverts the intent of a reward scale.
            plan.XpScale = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio, limits.XpScale);
            plan.LumScale = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio, limits.LumScale);
            // Resolved once here and stamped on every creature. Nothing is rolled: the per-kill roll is taken
            // at the death path off ThreadSafeRandom, so the gem's seeded rng is untouched and a gem carrying
            // only affinity modifiers produces a bit-identical population to one carrying none.
            //
            // ADDITIVE axis (neutral value 0): effective = ratio * raw, clamped back into [0, 1] since it is
            // a per-kill probability.
            plan.SalvageAffinities = DungeonRewardMath.SalvageAffinities(spec, mods)
                .Select(a => (a.MaterialId, a.BaseWcid, Chance: Math.Clamp(DungeonRewardMath.ApplyAdditiveScale(rewardScaleRatio, a.Chance), 0.0, 1.0)))
                .ToList();
            // Loot QUANTITY is multiplicative (neutral 1.0, a count multiplier); loot QUALITY is additive
            // (neutral 0.0, a bonus added to LootQualityMod). LootQuantityMultiplier is already clamped to
            // [1, limits.LootQuantityCap] before the ratio is applied, on the same "cap bounds the gem's own
            // product, the ratio sits outside it" reasoning as XpScale/LumScale above.
            var scaledLootQuantityMult = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio,
                DungeonRewardMath.LootQuantityMultiplier(spec, mods, limits.LootQuantityCap));
            var scaledLootQualityBonus = DungeonRewardMath.ApplyAdditiveScale(rewardScaleRatio, DungeonRewardMath.LootQualityBonus(spec, mods));
            plan.Profile = DungeonRewardMath.BuildProfile(spec.Tier, scaledLootQualityBonus, limits.LootQualityCap, limits.LootTierCap,
                scaledLootQuantityMult);
            // Carried alongside the profile rather than discarded, so the boss cache can size its own roll
            // from the SAME multiplier the run's creatures were built with. BuildProfile has already folded
            // it into the profile's maximum item counts; the cache needs the scalar itself, because it rolls
            // a fixed COUNT of items rather than one profile draw.
            plan.LootQuantityMult = scaledLootQuantityMult;

            // The uplift tag, in ONE place so every construction site of a DungeonSpawnPlanEntry below gets
            // the identical rule: a pick whose LIVE level sits below the natural band's low edge could only
            // have been reached by the extension, and carries the GEM's level as its normalization target;
            // everything else carries 0 and is untouched by the whole feature. When nothing widened, no pick
            // can be below that edge by construction, so an unwidened run tags nothing.
            int UpliftFor(uint wcid) => levelOf(wcid) < plan.NaturalBandLow ? spec.Level : 0;

            // ---- family ----
            SpeciesTableDef family = null;
            var familyNoted = false;

            // The per-role draw pools and the bands they came from, declared out here because the boss
            // section below re-uses BOTH: the promotion candidate is drawn from the elite pool, and the I5
            // duplicate swap re-draws from whichever pool the duplicated slot originally came from. Resolving
            // them twice would risk the swap drawing from a different band than the draw did.
            var trashBand = band;
            var eliteBand = band;
            var trashPool = new List<uint>();
            var elitePool = new List<uint>();

            // Family-eligibility widening. Returns the natural band untouched when the band already fields
            // MinEligibleFamilies tables, when the threshold is disabled, or when the floor is 1.0 - so this
            // call is what makes I-A and I-E true at the source rather than by a branch further down.
            var eligibilityBand = DungeonRosterSelector.ResolveEligibilityBand(speciesForSelection, spec.Level, levelOf, band,
                lowFloor, limits.MinEligibleFamilies);

            if (eligibilityBand.Low < band.Low)
                plan.Notes.Add($"band: only {DungeonRosterSelector.EligibleFamilyCount(speciesForSelection, spec.Level, levelOf, band)} " +
                               $"family/families in the natural band; low edge widened from x{band.Low:0.##} to x{eligibilityBand.Low:0.##} " +
                               $"(want {limits.MinEligibleFamilies})");

            try
            {
                family = DungeonRosterSelector.PickFamily(speciesForSelection, spec.Family, dungeon.Families, dungeon.CreatureTypes, spec.Level, levelOf, rng, eligibilityBand);
            }
            catch (ArgumentException ex)
            {
                // A gem that FORCES a family the tables cannot satisfy. PickFamily throws rather than
                // silently substituting, and that refusal is content feedback, not a crash - it becomes a
                // note and the run opens with no trash.
                plan.Notes.Add($"family: {ex.Message}");
                familyNoted = true;
            }

            if (family == null)
            {
                if (!familyNoted)
                    plan.Notes.Add($"family: no species table has a trash member in the level-{spec.Level} band");
            }
            else
            {
                plan.FamilyId = family.Id;

                // Per-family widening, starting from the band the family was SELECTED at rather than from the
                // natural band - a family that only became eligible through the widening above may have no
                // in-band member at all at the natural band, and restarting the search there could hand back
                // an empty pool and open the run with no trash.
                trashBand = DungeonRosterSelector.ResolveFamilyTrashBand(family, spec.Level, levelOf, eligibilityBand, lowFloor, limits.MinFamilyPool);
                eliteBand = DungeonRosterSelector.ResolveFamilyEliteBand(family, spec.Level, levelOf, eligibilityBand, lowFloor);

                trashPool = DungeonRosterSelector.PoolFor(family, spec.Level, DungeonRole.Trash, levelOf, trashBand);

                // ElitePoolFor, NOT PoolFor: the empty-elite fallback has to land on the TRASH band's pool,
                // and PoolFor would take it against the ELITE band's trash members instead - which for a
                // family with no elite at any width is the widest band the floor allows, so the elite slots
                // would draw from a deeper pool than the trash slots in the same run. Every consumer below
                // spells the fallback out against trashPool for that reason.
                elitePool = DungeonRosterSelector.ElitePoolFor(family, spec.Level, levelOf, eliteBand);

                plan.EffectiveBandLow = DungeonRosterSelector.TrashBand(spec.Level, trashBand).Low;

                if (trashBand.Low < eligibilityBand.Low)
                    plan.Notes.Add($"band: family {family.Id} had {DungeonRosterSelector.PoolFor(family, spec.Level, DungeonRole.Trash, levelOf, eligibilityBand).Count} " +
                                   $"distinct trash wcid(s) at x{eligibilityBand.Low:0.##}; low edge widened to x{trashBand.Low:0.##} " +
                                   $"(want {limits.MinFamilyPool}, got {trashPool.Count})");

                var points = dungeon.Points.Where(p => p != null && p.Curated && !IsAnchor(p, dungeon.BossAnchor)).ToList();
                var countMult = DungeonRewardMath.RunValue(spec, mods, DungeonRewardMath.CountMult, 1.0);
                if (double.IsNaN(countMult) || countMult < 0) countMult = 1.0;

                // count_mult can only ever REMOVE slots relative to the curated points: it multiplies the
                // point count, then min() against both the point count and the tunable hard cap. A gem can
                // thin a dungeon out but can never invent a spawn location the curator did not place.
                var slots = Math.Min(points.Count, Math.Min(limits.MaxMonsters, (int)Math.Round(points.Count * countMult)));
                slots = Math.Max(0, slots);

                if (eliteBand.Low < eligibilityBand.Low)
                    plan.Notes.Add($"band: family {family.Id} had no elite in the x{eligibilityBand.Low:0.##} band; " +
                                   $"elite low edge widened to x{eliteBand.Low:0.##} ({elitePool.Count} in pool)");

                var eliteShare = DungeonRewardMath.RunValue(spec, mods, DungeonRewardMath.EliteShare, DefaultEliteShare);
                var picks = DungeonRosterSelector.PickPopulation(trashPool, elitePool, slots, eliteShare, rng);

                // Shuffle the points so a count_mult below 1, or a MaxMonsters cap, does not always drop the
                // same (deepest) rooms - the curated list comes out of the tool in BFS order.
                var shuffled = points.OrderBy(_ => rng.Next()).ToList();
                for (var i = 0; i < picks.Count && i < shuffled.Count; i++)
                    plan.Entries.Add(new DungeonSpawnPlanEntry(picks[i].Wcid, picks[i].Role, shuffled[i], UpliftFor(picks[i].Wcid)));

                if (picks.Count == 0)
                    plan.Notes.Add($"family {family.Id}: population came back empty");
            }

            // ---- boss ----
            var bossLow = spec.Level * BossLevelWindowLow;
            var bossHigh = spec.Level * BossLevelWindowHigh;

            // Families may be NULL, not just empty: ThreadDungeonStore normalises a boss row's Modifiers but
            // not its Families, and System.Text.Json overwrites the "= new List<string>()" initialiser with
            // null when bosses.json writes "families": null. Null means the same as empty here - any family.
            //
            // THE FIT PREDICATE BINDS THE CURATED SHORTLIST TOO. A curated boss stands on the anchor, which is
            // the DEEPEST curated point in the dungeon, so a boss that cannot path there is the same doorway
            // exploit as any trash creature - and a worse one, since the boss is the fight the run is built
            // around. Emptying the shortlist is not a dead end: it falls into the promotion branch below,
            // which already exists and is already exercised. Note that the highest curated boss level shipped
            // is 325, so every gem above 325 promotes from the elite pool regardless of this filter.
            var bosses = store.Bosses
                .Where(b => b.Level >= bossLow && b.Level <= bossHigh)
                .Where(b => (b.Families?.Count ?? 0) == 0 || (plan.FamilyId != null && b.Families.Contains(plan.FamilyId)))
                .Where(b => fitsDungeon == null || fitsDungeon(b.Wcid))
                .ToList();

            // The curated pool is a PREFERENCE, not the guarantee. When it can field nobody for this gem, the
            // run's own family promotes its best member rather than going bossless - a run with no boss can
            // only be cleared by grinding the trash fraction, and the top rungs would lose their headline
            // fight entirely now that the level window no longer reaches below the gem level.
            IReadOnlyList<string> bossModifiers = null;
            var promoted = false;

            if (dungeon.BossAnchor == null)
            {
                plan.Notes.Add($"boss: none eligible for level {spec.Level} family {plan.FamilyId ?? "none"} (dungeon has no bossAnchor)");
            }
            else if (bosses.Count > 0)
            {
                var boss = bosses[rng.Next(bosses.Count)];
                plan.BossWcid = boss.Wcid;
                bossModifiers = boss.Modifiers;
            }
            else if (family != null)
            {
                // I5 at the source: hand the drawn pack in so promotion skips a wcid already standing in the
                // room when the family has anyone else to offer.
                var drawn = new HashSet<uint>(plan.Entries.Select(e => e.Wcid));

                // Promotion draws from the pool an ELITE slot would draw from, exactly as it always has -
                // now the WIDENED elite pool, with the same trash fallback PoolFor performs when a family has
                // no elite in band at any width. A promoted candidate below the natural low edge is tagged
                // for uplift like any other entry (I-G): the normalization and the boss uplift both apply,
                // and the boss level stamp's own four floors are unchanged.
                var candidate = DungeonRosterSelector.PickBossCandidate(elitePool.Count > 0 ? elitePool : trashPool,
                    levelOf, rng, drawn);

                if (candidate != 0)
                {
                    plan.BossWcid = candidate;
                    bossModifiers = PromotedBossModifiers;
                    promoted = true;
                    plan.Notes.Add($"boss: no curated row eligible for level {spec.Level}; promoted wcid {candidate} (level {levelOf(candidate)}) from family {family.Id}");
                }
                else
                {
                    plan.BossNoCandidate = true;
                    plan.Notes.Add($"boss: none eligible for level {spec.Level} family {plan.FamilyId ?? "none"} and family {family.Id} could field no candidate");
                }
            }
            else
            {
                // Same shape as the branch above and so the same flag: a boss anchor exists, the curated
                // window was empty, and there was not even a family to promote from.
                plan.BossNoCandidate = true;
                plan.Notes.Add($"boss: none eligible for level {spec.Level} family {plan.FamilyId ?? "none"}");
            }

            if (plan.BossWcid != 0)
            {
                plan.BossHealthMultiplier = DungeonRewardMath.HealthMultiplier(spec, mods, bossModifiers);
                plan.BossDamageRating = DungeonRewardMath.RatingTotal(spec, mods, bossModifiers, DungeonRewardMath.DamageRating);
                plan.BossDamageResistRating = DungeonRewardMath.RatingTotal(spec, mods, bossModifiers, DungeonRewardMath.DamageResistRating);

                // I5, second half, and it binds CURATED rows too. wcid 41229 is simultaneously a bosses.json
                // row and a role-1 virindi member, so without this a run could stand the Apostate Reaving
                // Master on the boss anchor and a byte-identical copy of it three rooms away. Re-draw those
                // slots from the same pool they came from; a single-member pool has nowhere to go, which is
                // exactly the "more than one eligible member" escape the invariant allows.
                var swapped = 0;

                if (family != null)
                {
                    for (var i = 0; i < plan.Entries.Count; i++)
                    {
                        var e = plan.Entries[i];

                        if (e.Role == DungeonRole.Boss || e.Wcid != plan.BossWcid)
                            continue;

                        // The SAME pool the slot was drawn from, widening included - re-resolving the band
                        // here instead would re-draw a widened slot out of the narrow natural pool and could
                        // find it empty.
                        var source = e.Role == DungeonRole.Trash ? trashPool : (elitePool.Count > 0 ? elitePool : trashPool);
                        var pool = source.Where(w => w != plan.BossWcid).ToList();

                        if (pool.Count == 0)
                            continue;

                        var replacement = pool[rng.Next(pool.Count)];
                        plan.Entries[i] = new DungeonSpawnPlanEntry(replacement, e.Role, e.Point, UpliftFor(replacement));
                        swapped++;
                    }
                }

                if (swapped > 0)
                    plan.Notes.Add($"boss: wcid {plan.BossWcid} also drawn as trash/elite; re-drew {swapped} slot(s) so the boss is unique");

                // I1. Four terms, all floors, so the result is >= every one of them:
                //   - the boss weenie's own authored level, because the uplift may only ever RAISE it;
                //   - one above the toughest thing in the pack, which is the invariant the owner asked for;
                //   - one above the gem's own advertised level;
                //   - the gem level times the configured margin, which is what gives a boss headroom in a
                //     run whose pack happened to draw low.
                var packLevel = plan.Entries.Where(e => e.Role != DungeonRole.Boss).Select(e => levelOf(e.Wcid)).DefaultIfEmpty(0).Max();
                var margin = double.IsNaN(limits.BossLevelMargin) || limits.BossLevelMargin < 1.0 ? 1.0 : limits.BossLevelMargin;

                var bossLevel = (double)levelOf(plan.BossWcid);
                bossLevel = Math.Max(bossLevel, packLevel + 1);
                bossLevel = Math.Max(bossLevel, spec.Level + 1);
                // The 1e-9 epsilon absorbs float error in level * margin the same way
                // ThreadDungeonRun.ClearTargetLocked does for its own ceiling: 100 * 1.10 evaluates to
                // 110.00000000000001, which would otherwise ceil to 111 and make the shipped margin quietly
                // one level stronger than the number it is configured with.
                bossLevel = Math.Max(bossLevel, Math.Ceiling(spec.Level * margin - 1e-9));

                plan.BossLevel = (int)Math.Clamp(bossLevel, 1, int.MaxValue);

                // I3 and I4. FLOORS, never products: max(what the modifiers alone give, pack + floor). That
                // keeps the uplift idempotent and monotone, and stops a boss_enraged gem from compounding
                // with an unconditional bonus into a boss nobody can trade with.
                plan.BossDamageRating = Math.Max(plan.BossDamageRating, plan.DamageRating + limits.BossDamageRatingFloor);
                plan.BossDamageResistRating = Math.Max(plan.BossDamageResistRating, plan.DamageResistRating + limits.BossDamageResistFloor);

                // I2's ratio, carried to the spawner because only it can observe real health. Zero means
                // "axis disabled", which is what DungeonRewardMath.BossHealthTarget reads out of any value
                // <= 0. The garbage cases are already handled where the tunable is READ
                // (ThreadDungeonSpawner.SanitizeBossDial, which falls back to the compiled default), so a
                // NaN or negative can only reach here from a limits object constructed in code; treat it as
                // disabled rather than propagating a NaN into the spawner's arithmetic.
                plan.BossHealthFloorRatio = double.IsNaN(limits.BossHealthFloorRatio) || limits.BossHealthFloorRatio < 0
                    ? 0.0
                    : limits.BossHealthFloorRatio;

                // I6: the boss entry is the LAST element of Entries, and that is load-bearing for I2 - it is
                // what makes the pack's observed health maximum known by the time the spawner places the boss.
                //
                // The boss entry carries the uplift tag on the same rule as every other entry (I-G). A
                // CURATED boss is drawn from bosses.json and its level window starts at 1.0x the gem level,
                // so it can never be tagged; a PROMOTED one comes out of the family's own pool and can be.
                // When it is, the spawner applies the band normalization AND the boss uplift, in that order,
                // and neither can lower the other because both are floors.
                plan.Entries.Add(new DungeonSpawnPlanEntry(plan.BossWcid, DungeonRole.Boss, dungeon.BossAnchor, UpliftFor(plan.BossWcid)));

                if (promoted)
                    plan.Notes.Add($"boss: promoted wcid {plan.BossWcid} stamped to level {plan.BossLevel} (pack max {packLevel}, gem {spec.Level})");
            }

            // ---- band standard ----
            // Resolved LAST and only when something was actually tagged: the sample walks every role-0 member
            // of every table and builds a profile for each, which is far more work than the two scalar reads
            // the rest of the plan needs, and a run that never widened its band must not pay for it. This is
            // also what keeps I-A and I-E cheap as well as correct - with the feature off the delegate is
            // never invoked at all.
            if (profileOf != null && plan.Entries.Any(e => e.UpliftLevel > 0))
            {
                // THE RAW DICTIONARY, for the same reason BandMedianHealth uses it: the band standard is what
                // a level-N creature in this band typically carries, a property of the BAND and not of who can
                // be placed in this dungeon. A dungeon-dependent standard would normalize the identical
                // uplifted creature to two different stat lines depending on where it spawned.
                plan.BandStandard = DungeonBandStandard.Compute(species, spec.Level, profileOf, band, lowFloor);

                plan.Notes.Add(plan.BandStandard.IsEmpty
                    ? $"uplift: {plan.Entries.Count(e => e.UpliftLevel > 0)} entry/entries tagged for level {spec.Level} but the band sample had no usable data; only the level is normalized"
                    : $"uplift: {plan.Entries.Count(e => e.UpliftLevel > 0)} entry/entries normalized to level {spec.Level} against a {plan.BandStandard.SampleCount}-member band standard " +
                      $"(sample low x{plan.BandStandard.SampleLowRatio:0.##}, dmg {plan.BandStandard.MaxBodyDamage}, armor {plan.BandStandard.MaxBaseArmor}, {plan.BandStandard.SkillMedians.Count} skill(s))");
            }

            return plan;
        }

        /// <summary>
        /// True when <paramref name="point"/> is the boss anchor. Compared by LOCATION, not by reference: the
        /// store reads "points" and "bossAnchor" out of the same file into separate objects, so the anchor
        /// that also appears in the points list arrives as a different instance holding the same cell and
        /// offset. A reference-only check would leave that point in the trash pool and stack a second
        /// creature inside the boss.
        /// </summary>
        private static bool IsAnchor(DungeonSpawnPointDef point, DungeonSpawnPointDef anchor)
        {
            if (anchor == null) return false;
            if (ReferenceEquals(point, anchor)) return true;

            return point.Cell == anchor.Cell
                && point.X == anchor.X
                && point.Y == anchor.Y
                && point.Z == anchor.Z;
        }

        /// <summary>
        /// True when a creature can actually be killed and so can satisfy a run's clear condition. A weenie
        /// with Attackable false, or one flagged NPC (PlayerKillerStatus.NPC, which shares its numeric value
        /// with Protected), cannot be reduced to zero health by a player, so placing one as a run's boss would
        /// leave the run permanently unclearable (ThreadDungeonRun's clear rule requires every spawned
        /// creature dead, boss included).
        /// </summary>
        public static bool IsKillable(bool? attackable, PlayerKillerStatus playerKillerStatus)
            => (attackable ?? true) && playerKillerStatus != PlayerKillerStatus.NPC;

        /// <summary>
        /// The retail value one kill of this creature is worth BEFORE anything the run does to it - the
        /// anchor both XP and luminance are priced off.
        ///
        /// Ruling R20 is REVERSED here (owner, 2026-09-06). Rewards used to be priced off the GEM's level so
        /// a lucky roster draw could not outpay the gem's advertised rate. The owner's requirement is now the
        /// opposite and simpler: a dungeon kill pays a fixed multiple of what killing THAT creature outside
        /// the dungeon pays, so a higher-level monster must be worth more and the baseline is exactly 1.0x
        /// retail. That makes the scalar measurable rather than entangled with a band-median fudge.
        ///
        /// <paramref name="xpOverride"/> is the creature's own PropertyInt.XpOverride. The ladder fallback is
        /// not theoretical: measured against ace_world on 2026-09-06, 1,490 of the 1,544 roster members in
        /// Content/events/axes/species carry XpOverride greater than zero and 54 do not, and without the
        /// fallback every one of those 54 would pay nothing at all.
        ///
        /// The Boss term is the second half. A PROMOTED boss is drawn from its family's own trash/elite
        /// roster, so it carries a trash-tier XpOverride while the spawner stamps it
        /// <paramref name="bossLevel"/>; anchoring purely on its weenie would pay boss difficulty at trash
        /// rates. Taking the max of the two never lowers a curated boss that is already worth more.
        ///
        /// <paramref name="upliftLevel"/> is the third term and it exists for exactly the same reason as the
        /// boss one. An entry the adaptive band reached below the natural low edge is placed with its level,
        /// skills, melee damage and body armour all normalized to the gem's own level, so it fights like a
        /// creature of that level; without this term it would still pay the XP of the level-200 weenie it was
        /// authored as inside a level-375 run, and the extension would read to a player as a stealth reward
        /// cut. THE LADDER DOES NEED TO COVER IT. The uplift target is always the GEM's level, which
        /// DungeonGemSpec.MaxLevel caps at 375, and the boss term reaches higher still - a boss level is a
        /// multiple of the gem level - so Content/dungeons/dynamic/modifiers.json carries rungs through 375
        /// and one more at 415 above it. Beyond the top rung LadderXp returns that rung's flat value
        /// (DungeonRewardMath.cs:89-91), so a ladder that stopped at 275 would silently pay every level above
        /// it the same 2,750,000 and the two terms would go inert exactly where they matter most. Any future
        /// ceiling raise needs new rungs here in the same commit.
        ///
        /// A max, like the boss term, so it never lowers a creature whose own XpOverride is already worth
        /// more - which is the ordinary case for a high-XpOverride weenie that merely sits a few levels under
        /// the edge. Luminance needs no term of its own: DungeonPopulationBuilder.LuminanceFor derives from
        /// this same baseXp, so raising the anchor here raises both on one curve.
        /// </summary>
        public static long BaseXp(IReadOnlyList<XpLadderRungDef> ladder, int? xpOverride, int creatureLevel, DungeonRole role, int bossLevel,
            int upliftLevel = 0)
        {
            var baseXp = xpOverride.HasValue && xpOverride.Value > 0
                ? xpOverride.Value
                : DungeonRewardMath.LadderXp(ladder, creatureLevel);

            if (upliftLevel > 0)
                baseXp = Math.Max(baseXp, DungeonRewardMath.LadderXp(ladder, upliftLevel));

            if (role == DungeonRole.Boss && bossLevel > 0)
                baseXp = Math.Max(baseXp, DungeonRewardMath.LadderXp(ladder, bossLevel));

            return baseXp;
        }

        /// <summary>XP a single kill of <paramref name="role"/> worth <paramref name="baseXp"/> awards under this plan.</summary>
        public static long XpFor(DungeonSpawnPlan plan, long baseXp, DungeonRole role)
            => DungeonRewardMath.XpForKill(baseXp, role, plan.XpScale, plan.XpMultiplier, plan.BossXpMultiplier);

        /// <summary>
        /// Luminance a single kill awards, derived from the same <paramref name="baseXp"/> anchor by the
        /// fork's luminance standard rather than from a flat award of its own.
        ///
        ///     lumBase = round-half-up(baseXp / 4000) to the nearest 50, and 0 below level 185
        ///     lum     = round(lumBase * RoleRate(role) * plan.LumScale * plan.LumMultiplier)
        ///
        /// Four things this shape is load-bearing for:
        ///
        /// 1. The rounding is HALF-UP, not .NET's default banker's rounding. The standard's own table is
        ///    computed half-up, and the two disagree at every odd multiple of 25 in the quotient: a level-265
        ///    creature's 2,500,000 XP gives 625 / 50 = 12.5, which is 650 half-up and 600 to-even.
        ///
        /// 2. <paramref name="baseXp"/> is the creature's BASE retail XP, never the XpOverride the spawner
        ///    goes on to stamp - by then that value already folds RoleRate, the scale and the XP modifier
        ///    product, so dividing it by 4,000 would apply all three twice and would silently turn every XP
        ///    modifier into a luminance modifier, leaving LumMultiplier and dynamic_dungeons_lum_mult_cap
        ///    vestigial.
        ///
        /// 3. The level gate is the DRAWN CREATURE's level, not the gem's. The standard's floor is per
        ///    creature, and with the award derived per creature that is the only consistent reading.
        ///
        /// 4. The result is NOT re-snapped to the 50-grid. RoleRate 1.5 and a 2.7 scale do not land on it
        ///    (200 x 1.5 x 2.7 = 810), and snapping to 800 would quietly change the elite rate from 1.5 to
        ///    1.48. Round to 50 once, at the quotient.
        /// </summary>
        public static int LuminanceFor(DungeonSpawnPlan plan, long baseXp, int creatureLevel, DungeonRole role)
        {
            if (creatureLevel < LuminanceBaseLevel) return 0;

            var lumBase = Math.Round(baseXp / (double)LuminanceXpPerLum / LuminanceRounding, MidpointRounding.AwayFromZero) * LuminanceRounding;
            var lum = lumBase * DungeonRewardMath.RoleRate(role) * plan.LumScale * plan.LumMultiplier;

            return (int)Math.Clamp(Math.Round(lum, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        }
    }
}
