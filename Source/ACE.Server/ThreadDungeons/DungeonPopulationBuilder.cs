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

        /// <summary>
        /// Floor on NON-BOSS creatures placed in one run (dynamic_dungeons_min_monsters_per_run). When a
        /// dungeon has fewer curated points than this, the extra creatures share curated points rather than
        /// going unplaced - see the count_mult/slots comment further down. 0 disables the floor. Never
        /// exceeds <see cref="MaxMonsters"/> - a misconfigured minimum above the cap is silently bounded by
        /// it rather than overriding it.
        /// </summary>
        public readonly int MinMonsters;

        public readonly double XpCap;
        public readonly double LumCap;
        public readonly int LootTierCap;
        public readonly double LootQualityCap;
        /// <summary>Ceiling on the product of the gem's lootQuantityMult values (dynamic_dungeons_loot_quantity_cap).</summary>
        public readonly double LootQuantityCap;

        /// <summary>
        /// Shrinks ONLY the bonus above neutral of a gem's rolled reward-modifier PRODUCTS - XpMultiplier,
        /// BossXpMultiplier, LumMultiplier, LootQuantityMultiplier - via
        /// <see cref="DungeonRewardMath.ApplyModifierRewardScale"/>: effective = 1 + s * (product - 1)
        /// (dynamic_dungeons_modifier_reward_scale). Applied AFTER each axis's own cap (XpCap/LumCap/
        /// LootQuantityCap), so every gem pays the same proportional cut of its own capped bonus, and - on the
        /// loot-quantity axis only - BEFORE the gem-level reward-scale ratio (<see cref="RewardScaleAnchor"/>
        /// and friends), which is a separate, level-keyed scalar this dial never touches; the two compose in
        /// that order (see DungeonPopulationBuilder.Build).
        ///
        /// 1.0 is neutral - a no-op, byte-identical to before this dial existed. 0.0 makes every modifier pay
        /// exactly retail (1.0) on these four axes alone; it does not touch dynamic_dungeons_xp_scale/
        /// _lum_scale, the gem-level reward-scale ratio itself, salvage-affinity chances, the loot QUALITY
        /// bonus, survey rewards, or a modifier's monster-side effect (health, damage, resists, and so on).
        /// </summary>
        public readonly double ModifierRewardScale;

        /// <summary>
        /// How many times the toughest non-boss creature in the run the boss must be worth, at minimum
        /// (dynamic_dungeons_boss_health_floor_ratio). A FLOOR, never a multiplier stacked on the gem's own
        /// modifiers: the boss ends up at max(what its modifiers alone would give, this ratio times the
        /// observed pack maximum). Stacking instead would put a boss_guarded + hardy run near 24x base and
        /// risk a fight that cannot finish inside the run's TTL.
        /// </summary>
        public readonly double BossHealthFloorRatio;

        /// <summary>
        /// Multiple of the BAND STANDARD health a non-boss creature must reach, applied BEFORE the gem's own
        /// health multiplier (dynamic_dungeons_trash_health_floor_ratio).
        ///
        /// "Band standard" is <see cref="DungeonHealthCurve"/>'s target for the gem's level while
        /// <see cref="HealthCurveEnabled"/> is on, and DungeonRosterSelector.BandMedianHealth's measured
        /// median when it is off. The ratio's MEANING is unchanged either way - half of what a creature at
        /// this level ought to carry - and it still catches an under-levelled draw from a widened band, which
        /// the proportional normalization alone cannot: normalization scales a creature relative to the
        /// sample, and a creature far below the sample stays far below it afterwards.
        ///
        /// Floor-then-multiply, the opposite order from
        /// <see cref="BossHealthFloorRatio"/>: the owner's ruling is "scaling up health to fit band standards
        /// BEFORE other multipliers are added", because this floor measures a BAND STANDARD (what a creature
        /// in this level range ought to carry, regardless of which family a run rolled) rather than a pack
        /// MAXIMUM observed after the fact. A FLOOR, never a multiplier stacked on the gem's own modifiers: a
        /// creature already at or above the floor is untouched.
        /// </summary>
        public readonly double TrashHealthFloorRatio;

        /// <summary>
        /// Master on/off for the band-standard health curve (dynamic_dungeons_health_curve). False reproduces
        /// today's behaviour byte-for-byte: <see cref="DungeonHealthCurve"/> is never consulted,
        /// <see cref="DungeonSpawnPlan.HealthNormalizeRatio"/> stays 0 (a no-op at the spawner), and
        /// <see cref="DungeonSpawnPlan.TrashHealthFloor"/> is computed from the measured band median exactly
        /// as it was before the curve existed.
        /// </summary>
        public readonly bool HealthCurveEnabled;

        /// <summary>
        /// The curve's low anchor (dynamic_dungeons_health_curve_anchor_low): the health the curve passes
        /// through at <see cref="DungeonHealthCurve.AnchorLowLevel"/>. See
        /// <see cref="DungeonHealthCurve"/> for the equation and for why the per-level ratio is derived from
        /// this and <see cref="HealthCurveAnchorHigh"/> rather than configured separately.
        /// </summary>
        public readonly double HealthCurveAnchorLow;

        /// <summary>
        /// The curve's high anchor (dynamic_dungeons_health_curve_anchor_high): the health the curve passes
        /// through at <see cref="DungeonHealthCurve.AnchorHighLevel"/>. No longer the value it clamps to: since
        /// 2026-10-08 the curve extrapolates on the same ratio up to <see cref="HealthCurveTopLevel"/>.
        /// </summary>
        public readonly double HealthCurveAnchorHigh;

        /// <summary>
        /// The level at and above which the health curve clamps (dynamic_dungeons_health_curve_top_level),
        /// sanitized by <see cref="DungeonHealthCurve.SanitizeTopLevel"/> into [375, 500]. 500 (the default, the
        /// run ceiling) extrapolates the curve over every playable run level; 375 restores the pre-2026-10-08
        /// clamp exactly. See DungeonHealthCurve's class comment for the owner ruling behind the extrapolation.
        /// </summary>
        public readonly int HealthCurveTopLevel;

        /// <summary>
        /// Master switch for reach-up (dynamic_dungeons_reach_up, owner ruling 2026-10-08). Above the pivot P =
        /// floor(authored top / band high) - 326 on the shipped roster - the builder reads every creature at
        /// its PROJECTED level round(raw x L / P), so a run at level L draws the creatures that fill the pivot's
        /// natural band, scaled up to fill L's, and stamps each with its projected level
        /// (<see cref="DungeonSpawnPlanEntry.StampLevel"/>). False is k = 1 everywhere and a plan byte-identical
        /// to the builder before reach-up existed. At and below the pivot k is 1 either way.
        /// </summary>
        public readonly bool ReachUp;

        /// <summary>
        /// Master switch for the stat curve (dynamic_dungeons_stat_curve, owner ruling 2026-10-08): above the
        /// pivot the band standard is <see cref="DungeonStatCurve"/>'s log-linear extrapolation of the measured
        /// standard at the pivot, rather than a sample that flattens at the top of the authored roster. False
        /// restores the flattening standard and leaves a stamped creature's authored stats unscaled.
        /// </summary>
        public readonly bool StatCurve;

        /// <summary>
        /// The defense softening factor (dynamic_dungeons_defense_curve_rate_above_375, owner ruling
        /// 2026-10-08): above MONSTER level 375 the effective melee, missile and magic defense standard grows at
        /// this fraction of its fitted rate. 0.5 by default; 1.0 is the unsoftened curve and 0 freezes effective
        /// defense at its level-375 value. Sanitized to [0, 1] by <see cref="DungeonStatCurve.SanitizeDefenseRate"/>.
        /// </summary>
        public readonly double DefenseCurveRateAbove375;

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
        /// Deliberately NOT pinned to <see cref="DungeonGemSpec.MaxGemLevel"/>: it was set to 300 while the
        /// ceiling was 275, so that a ceiling raise would not silently devalue every existing gem.
        ///
        /// THE 2026-09-09 RAISE TO 375 PUT THE CEILING ABOVE THE ANCHOR, and the anchor was deliberately left
        /// where it is. The consequence is not a devaluation - it is the opposite: dynamic_dungeons_reward_
        /// scale_cap defaults to 0, which means UNCAPPED, so gems above 300 now scale past 1.0 rather than
        /// stopping there. That is the behaviour the missing ceiling was written for. Whether 300 is still
        /// the right anchor for a 375 ceiling is a TUNING question, not a correctness one, and it is the
        /// repo owner's call - it is a live tunable either way. The 2026-10-08 run ceiling
        /// (<see cref="DungeonGemSpec.MaxRunLevel"/>, 500) rides the same uncapped curve: a pressed 410 run
        /// reads (410 / 300)^2.87, about 2.45, with no edit here.
        /// </summary>
        public readonly double RewardScaleAnchor;

        /// <summary>The k of (level / anchor)^k (dynamic_dungeons_reward_scale_exponent).</summary>
        public readonly double RewardScaleExponent;

        /// <summary>Lower bound on the reward-scale ratio (dynamic_dungeons_reward_scale_floor).</summary>
        public readonly double RewardScaleFloor;

        /// <summary>
        /// The k of the modifier magnitude curve s = min(1, (level / 185)^k) (dynamic_dungeons_modifier_level_
        /// exponent) - see <see cref="DungeonModifierLevelScale"/>. Carried rather than read, on the same "the
        /// builder is pure" contract as <see cref="RewardScaleExponent"/>. k &lt;= 0 turns the curve off, so a
        /// default(DungeonPopulationLimits) (k = 0) builds exactly the full-strength plan it always did; the
        /// constructor defaults to the shipped <see cref="DefaultModifierLevelExponent"/>.
        /// </summary>
        public readonly double ModifierLevelExponent;

        /// <summary>
        /// Upper bound on the reward-scale ratio (dynamic_dungeons_reward_scale_cap). Zero or less means
        /// uncapped - deliberate, so growth above the anchor is unbounded until an admin sets an explicit
        /// safety valve.
        /// </summary>
        public readonly double RewardScaleCap;

        /// <summary>
        /// Ceiling on a run's PER-KILL salvage-affinity chance, after the affinity's OWN reward-scale ratio is
        /// applied (dynamic_dungeons_salvage_affinity_chance_cap) - see DungeonPopulationBuilder.Build. Salvage
        /// affinity deliberately does NOT share <see cref="RewardScaleCap"/>: it is scaled by
        /// <see cref="DungeonRewardMath.RewardScaleRatio"/> using the SAME anchor/exponent/floor as XP/
        /// luminance/loot-quantity but with NO cap on the ratio itself (RewardScaleRatio's own cap argument is
        /// always passed as 0/uncapped for this axis), so affinity keeps growing with gem level above the
        /// anchor regardless of what dynamic_dungeons_reward_scale_cap is set to for the other three axes. This
        /// dial is the one and only ceiling on the RESULT. 1.0 (default) makes this a no-op against today's
        /// [0, 1] chance domain - merging with the prod default of dynamic_dungeons_reward_scale_cap = 0
        /// (uncapped) changes nothing. NaN or a negative value reads as the default; anything above 1.0 reads
        /// as 1.0 (the chance domain's own ceiling, same reasoning as ModifierRewardScale's MaxModifierRewardScale).
        /// </summary>
        public readonly double SalvageAffinityChanceCap;

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

        // ---- boss normalization, trait strip, boss-row selection (owner requirements 2026-09-13) ----------
        //
        // Every one of these SHIPS ON (owner ruling 2026-09-13). "0 = off" on a numeric dial is an admin escape
        // hatch only; the compiled const, the PropertyManager registration and the spawner's read fallback all
        // carry the same shipped value, and ThreadDungeonSpawnerTunableTests pins registration == const.

        /// <summary>
        /// Master switch for boss normalization (dynamic_dungeons_boss_normalize). ON: the boss's health is
        /// max(<see cref="BossHealthBandRatio"/> x the band-standard curve, <see cref="BossHealthPackMargin"/> x
        /// the toughest non-boss base in the run's pools) times its own modifiers, its offense and defense are
        /// SET (two-way) to the band standard, its authored ratings are zeroed before the plan's rating floors
        /// apply, its level stamp drops the weenie-authored term and its XP prices the ladder at the stamped
        /// level only. OFF reproduces the pre-normalization boss exactly.
        /// </summary>
        public readonly bool BossNormalize;

        /// <summary>R in bossBase = max(R x T(gem), M x PoolMaxBase) (dynamic_dungeons_boss_health_band_ratio). 0 = use M only.</summary>
        public readonly double BossHealthBandRatio;

        /// <summary>
        /// M in the same formula (dynamic_dungeons_boss_health_pack_margin). A positive value below 1.0 reads as
        /// 1.0, because the boss must outrank its pack; 0 = use R only.
        /// </summary>
        public readonly double BossHealthPackMargin;

        /// <summary>Multiple of the band standard the boss's attack skills and melee damage are SET to (dynamic_dungeons_boss_offense_band_ratio). 0 = leave the axis authored.</summary>
        public readonly double BossOffenseBandRatio;

        /// <summary>Multiple of the band standard the boss's defense skills and body armour are SET to (dynamic_dungeons_boss_defense_band_ratio). 0 = leave the axis authored.</summary>
        public readonly double BossDefenseBandRatio;

        /// <summary>
        /// Master switch for the combat-trait strip and category-immunity guard on EVERY run creature
        /// (dynamic_dungeons_strip_combat_traits). Runs before the gem's own shield_hollow/hollow writes, so
        /// those still apply as advertised.
        /// </summary>
        public readonly bool StripCombatTraits;

        /// <summary>F: a whole physical or magic resist group whose best member is below this is raised to it (dynamic_dungeons_category_resist_floor). 0 = off.</summary>
        public readonly double CategoryResistFloor;

        /// <summary>C: physical ArmorModVs whose SMALLEST member exceeds this are scaled down so it equals this (dynamic_dungeons_category_armor_mod_ceiling). 0 = off.</summary>
        public readonly double CategoryArmorModCeiling;

        /// <summary>
        /// The additive offset in a non-boss creature's EFFECTIVE melee/missile/magic defense skill cap
        /// (dynamic_dungeons_defense_skill_cap_offset): band effective median (attribute-formula contribution
        /// plus authored InitLevel) plus this. 0 caps exactly at the median; a negative value disables the axis
        /// - unlike every other 0-disables dial on this struct, because 0 is a meaningful cap here.
        /// </summary>
        public readonly double DefenseSkillCapOffset;

        /// <summary>
        /// With <see cref="BossNormalize"/> also on, bosses.json rows with an empty families list skip the
        /// [1.0, 1.6] level window and are eligible at every gem level (dynamic_dungeons_boss_any_all_bands).
        /// Family rows keep the window.
        /// </summary>
        public readonly bool BossAnyAllBands;

        /// <summary>
        /// W: draw weight of a bosses.json row whose families list names the run's family; any-family rows weigh
        /// 1 (dynamic_dungeons_boss_family_row_weight). Values below 1 read as 1; exactly 1 keeps the legacy
        /// uniform draw.
        /// </summary>
        public readonly double BossFamilyRowWeight;

        public const bool DefaultBossNormalize = true;
        public const double DefaultBossHealthBandRatio = 3.0;
        public const double DefaultBossHealthPackMargin = 1.25;
        public const double DefaultBossOffenseBandRatio = 1.0;
        public const double DefaultBossDefenseBandRatio = 1.0;
        public const bool DefaultStripCombatTraits = true;
        public const double DefaultCategoryResistFloor = 0.5;
        public const double DefaultCategoryArmorModCeiling = 2.0;
        /// <summary>
        /// Owner default (2026-09-17): 100 above the band's EFFECTIVE median, replacing the retired
        /// InitLevel-only ratio cap of 2.0x. The old ratio could not act on attribute-heavy retail weenies
        /// (wcid 46700 Crazed Olthoi authors ~1000 in every primary attribute and an authored melee defense
        /// InitLevel of only 320, for an effective ~987 that a 2.0x InitLevel cap of 640 never touched).
        /// </summary>
        public const double DefaultDefenseSkillCapOffset = 100.0;
        public const bool DefaultBossAnyAllBands = true;
        public const double DefaultBossFamilyRowWeight = 5.0;

        /// <summary>Default floor on non-boss creatures placed in one run (dynamic_dungeons_min_monsters_per_run).</summary>
        public const int DefaultMinMonsters = 40;

        private readonly GroupScaling group;

        /// <summary>
        /// The run's lock-time Group Threads snapshot (Docs/Threads/GROUP-THREADS-DESIGN.md sections 5 and 6.1).
        /// Never null: an unset value - including a default(DungeonPopulationLimits) - reads as
        /// <see cref="GroupScaling.Solo"/>, and every group-only step in the builder is guarded on
        /// <see cref="GroupScaling.IsGroup"/>, so a solo plan is built exactly as it was before groups existed.
        /// </summary>
        public GroupScaling Group => group ?? GroupScaling.Solo;

        /// <summary>
        /// How many other curated points a creature refused at its own spawn point is retried at
        /// (dynamic_dungeons_placement_fallback_attempts). Read by the spawner only - placement is not part of
        /// the pure plan - but held here with the rest of the shared defaults.
        /// </summary>
        public const int DefaultPlacementFallbackAttempts = 5;

        /// <summary>Typo ceilings for the dials above, on the same reasoning as <see cref="MaxBossHealthFloorRatio"/>.</summary>
        public const double MaxBossHealthBandRatio = 50.0;
        public const double MaxBossHealthPackMargin = 10.0;
        public const double MaxBossBandRatio = 5.0;
        public const double MaxCategoryResistFloor = 1.0;
        public const double MaxCategoryArmorModCeiling = 10.0;
        /// <summary>
        /// Typo ceiling for the offset. Effective defense skills observed across the shipped roster top out
        /// well under 1200 (the Crazed Olthoi case above is close to the worst of it), so 5000 is generous
        /// headroom above any real value while still catching a fat-fingered value in the tens of thousands
        /// from pinning every non-boss defense skill at a number nothing in the run can dent.
        /// </summary>
        public const double MaxDefenseSkillCapOffset = 5000.0;
        public const double MaxBossFamilyRowWeight = 100.0;
        public const int MaxPlacementFallbackAttempts = 50;

        /// <summary>The compiled defaults, shared with the PropertyManager registrations so the two cannot drift.</summary>
        public const double DefaultBossHealthFloorRatio = 4.0;

        /// <summary>The compiled default for the trash health floor ratio (owner ruling 2026-09-07).</summary>
        public const double DefaultTrashHealthFloorRatio = 0.5;

        /// <summary>
        /// The compiled defaults for the band-standard health curve, referencing DungeonHealthCurve's own
        /// constants so the PropertyManager registration, this struct and the curve itself are provably one
        /// value each - the same shared-constant contract as the roster band edges below.
        /// </summary>
        public const bool DefaultHealthCurveEnabled = true;
        public const double DefaultHealthCurveAnchorLow = DungeonHealthCurve.DefaultAnchorLow;
        public const double DefaultHealthCurveAnchorHigh = DungeonHealthCurve.DefaultAnchorHigh;
        public const int DefaultHealthCurveTopLevel = DungeonHealthCurve.DefaultTopLevel;

        /// <summary>
        /// The compiled defaults for the 2026-10-08 run-ceiling raise. Every one SHIPS ON (the fork convention
        /// that a new server setting defaults enabled), and each references its owning type's constant so the
        /// PropertyManager registration, this struct and the code are provably one value each.
        /// </summary>
        public const bool DefaultReachUp = true;
        public const bool DefaultStatCurve = true;
        public const double DefaultDefenseCurveRateAbove375 = DungeonStatCurve.DefaultDefenseRateAbove375;
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

        /// <summary>Default of dynamic_dungeons_uplift_attributes: the Threads-only attribute and effective-skill uplift (BandUplift.RaiseAttributes).</summary>
        public const bool DefaultUpliftAttributes = true;

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

        /// <summary>The compiled default for <see cref="ModifierLevelExponent"/>, shared with the PropertyManager registration.</summary>
        public const double DefaultModifierLevelExponent = DungeonModifierLevelScale.DefaultExponent;

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
        /// The compiled default for <see cref="ModifierRewardScale"/> (owner ruling: neutral on merge, set
        /// live at launch). 1.0 is also the domain's own ceiling - the axis is defined only on [0, 1] - so
        /// there is no separate Max constant the way the reward scalars above have MaxRewardScale.
        /// </summary>
        public const double DefaultModifierRewardScale = 1.0;

        /// <summary>Typo/domain ceiling for <see cref="ModifierRewardScale"/>; see its own doc comment.</summary>
        public const double MaxModifierRewardScale = 1.0;

        /// <summary>
        /// The compiled default for <see cref="SalvageAffinityChanceCap"/> (owner ruling: neutral on merge -
        /// with the prod default dynamic_dungeons_reward_scale_cap = 0 (uncapped), merging changes nothing;
        /// set to 0.25 live at launch). 1.0 is also the domain's own ceiling, same reasoning as
        /// DefaultModifierRewardScale/MaxModifierRewardScale above.
        /// </summary>
        public const double DefaultSalvageAffinityChanceCap = 1.0;

        /// <summary>Typo/domain ceiling for <see cref="SalvageAffinityChanceCap"/>; see its own doc comment.</summary>
        public const double MaxSalvageAffinityChanceCap = 1.0;

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
            double modifierRewardScale = DefaultModifierRewardScale,
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
            double salvageAffinityChanceCap = DefaultSalvageAffinityChanceCap,
            double trashHealthFloorRatio = DefaultTrashHealthFloorRatio,
            double rosterBandLow = DefaultRosterBandLow,
            double rosterBandHigh = DefaultRosterBandHigh,
            double bandLowFloorRatio = DefaultBandLowFloorRatio,
            int minEligibleFamilies = DefaultMinEligibleFamilies,
            int minFamilyPool = DefaultMinFamilyPool,
            bool upliftRename = DefaultUpliftRename,
            bool healthCurveEnabled = DefaultHealthCurveEnabled,
            double healthCurveAnchorLow = DefaultHealthCurveAnchorLow,
            double healthCurveAnchorHigh = DefaultHealthCurveAnchorHigh,
            bool bossNormalize = DefaultBossNormalize,
            double bossHealthBandRatio = DefaultBossHealthBandRatio,
            double bossHealthPackMargin = DefaultBossHealthPackMargin,
            double bossOffenseBandRatio = DefaultBossOffenseBandRatio,
            double bossDefenseBandRatio = DefaultBossDefenseBandRatio,
            bool stripCombatTraits = DefaultStripCombatTraits,
            double categoryResistFloor = DefaultCategoryResistFloor,
            double categoryArmorModCeiling = DefaultCategoryArmorModCeiling,
            double defenseSkillCapOffset = DefaultDefenseSkillCapOffset,
            bool bossAnyAllBands = DefaultBossAnyAllBands,
            double bossFamilyRowWeight = DefaultBossFamilyRowWeight,
            int minMonsters = DefaultMinMonsters,
            GroupScaling group = null,
            double modifierLevelExponent = DefaultModifierLevelExponent,
            int healthCurveTopLevel = DefaultHealthCurveTopLevel,
            bool reachUp = DefaultReachUp,
            bool statCurve = DefaultStatCurve,
            double defenseCurveRateAbove375 = DefaultDefenseCurveRateAbove375)
        {
            HealthCurveTopLevel = healthCurveTopLevel;
            ReachUp = reachUp;
            StatCurve = statCurve;
            DefenseCurveRateAbove375 = defenseCurveRateAbove375;
            this.group = group;
            ModifierLevelExponent = modifierLevelExponent;
            BossNormalize = bossNormalize;
            BossHealthBandRatio = bossHealthBandRatio;
            BossHealthPackMargin = bossHealthPackMargin;
            BossOffenseBandRatio = bossOffenseBandRatio;
            BossDefenseBandRatio = bossDefenseBandRatio;
            StripCombatTraits = stripCombatTraits;
            CategoryResistFloor = categoryResistFloor;
            CategoryArmorModCeiling = categoryArmorModCeiling;
            DefenseSkillCapOffset = defenseSkillCapOffset;
            BossAnyAllBands = bossAnyAllBands;
            BossFamilyRowWeight = bossFamilyRowWeight;

            MaxMonsters = maxMonsters;
            MinMonsters = minMonsters;
            XpCap = xpCap;
            LumCap = lumCap;
            LootTierCap = lootTierCap;
            LootQualityCap = lootQualityCap;
            LootQuantityCap = lootQuantityCap;
            ModifierRewardScale = modifierRewardScale;
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
            SalvageAffinityChanceCap = salvageAffinityChanceCap;
            TrashHealthFloorRatio = trashHealthFloorRatio;
            RosterBandLow = rosterBandLow;
            RosterBandHigh = rosterBandHigh;
            BandLowFloorRatio = bandLowFloorRatio;
            MinEligibleFamilies = minEligibleFamilies;
            MinFamilyPool = minFamilyPool;
            UpliftRename = upliftRename;
            HealthCurveEnabled = healthCurveEnabled;
            HealthCurveAnchorLow = healthCurveAnchorLow;
            HealthCurveAnchorHigh = healthCurveAnchorHigh;
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
        /// The reach-up stamp (owner ruling 2026-10-08), or 0 for "not stamped" - every entry of every run at or
        /// below the pivot, and every entry while dynamic_dungeons_reach_up is off.
        ///
        /// Above the pivot the builder reads each creature at its PROJECTED level round(raw x L / P), so a level-L
        /// run draws the creatures that fill the pivot's natural band. A pick whose projected level is above its
        /// authored one carries the projected level here, and the spawner stamps it: its level becomes this
        /// number, and (with dynamic_dungeons_stat_curve on) its attributes, effective skills, melee damage and
        /// body armour are scaled by the stat curve's ratio between its authored level and this one, so the pack
        /// keeps its authored spread while it climbs. It is always at most ceil(L x band high), because the pick
        /// was drawn from inside that band.
        ///
        /// MUTUALLY EXCLUSIVE WITH <see cref="UpliftLevel"/>: a pick whose projected level still falls below the
        /// natural low edge is uplifted to the gem level exactly as before reach-up existed, and carries no stamp.
        /// </summary>
        public int StampLevel { get; }

        /// <summary>
        /// <paramref name="upliftLevel"/> and <paramref name="stampLevel"/> are optional-defaulted to 0 so every
        /// pre-existing construction site and test keeps compiling and keeps meaning "not uplifted, not
        /// stamped", which is what they all were. Every construction site that copies an entry - the builder's
        /// boss-duplicate swap and the spawner's placement-fallback retry - must pass both through, or a retried
        /// creature silently fights at its authored level.
        /// </summary>
        public DungeonSpawnPlanEntry(uint wcid, DungeonRole role, DungeonSpawnPointDef point, int upliftLevel = 0, int stampLevel = 0)
        {
            Wcid = wcid;
            Role = role;
            Point = point;
            UpliftLevel = upliftLevel;
            StampLevel = stampLevel;
        }

        /// <summary>
        /// The highest level this entry is placed AT: its uplift or its stamp, whichever is set, or 0. The
        /// reward and level-stamp sites read this rather than either field alone.
        /// </summary>
        public int RaisedLevel => Math.Max(UpliftLevel, StampLevel);
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
        /// The modifier magnitude curve's s for this run (see <see cref="DungeonModifierLevelScale"/>), already
        /// folded into every modifier-derived field on this plan. Carried for logging and tests only; nothing
        /// downstream multiplies by it again. 1.0 at and above level 185.
        /// </summary>
        public double ModifierLevelScale { get; set; } = 1.0;

        /// <summary>
        /// The band-standard health floor (the band standard times
        /// DungeonPopulationLimits.TrashHealthFloorRatio), computed ONCE per run against the gem's own level
        /// and applied to every non-boss entry, elites included, BEFORE <see cref="HealthMultiplier"/> - the
        /// opposite order from the boss floor, which measures the pack's OBSERVED maximum AFTER multiplying.
        /// 0 when the axis is disabled (ratio 0), or - with the health curve off - when the cross-family trash
        /// pool has no usable health data; 0 is a no-op at the spawner (Math.Max leaves an untouched creature
        /// untouched).
        ///
        /// WITH THE CURVE ON, THE STANDARD IS <see cref="DungeonHealthCurve"/>'s TARGET, NOT THE MEASURED
        /// MEDIAN, so the floor is half of what the ladder says a creature at this level ought to carry rather
        /// than half of what the drawn band happens to hold. The curve needs no sample to state a floor, so
        /// this is still set when the band sample is empty and <see cref="HealthNormalizeRatio"/> is therefore
        /// 0.
        /// </summary>
        public uint TrashHealthFloor { get; set; }

        /// <summary>
        /// What every non-boss creature's authored health, and the boss's own authored base, is MULTIPLIED by
        /// before anything else touches it: <see cref="DungeonHealthCurve"/>'s target for the gem's level
        /// divided by the measured cross-family band median (DungeonRosterSelector.BandMedianHealth) for that
        /// same band. 0 MEANS NORMALIZE NOTHING - the same no-op convention <see cref="TrashHealthFloor"/> and
        /// <see cref="DungeonBandStandard.Empty"/> use - and is what a run gets when the master switch is off
        /// or when the band sample held no usable health data (no denominator, so nothing to normalize
        /// against).
        ///
        /// A RATIO AND NOT A TARGET, deliberately: every creature in the pack is scaled by the same factor, so
        /// the authored variety WITHIN a pack survives (a tougher-than-average family member stays tougher
        /// than average) while the pack as a whole lands on the ladder. Setting each creature to the target
        /// outright would flatten every room in the game to one number.
        ///
        /// Clamped to [0, <see cref="DungeonPopulationBuilder.MaxHealthNormalizeRatio"/>].
        /// </summary>
        public double HealthNormalizeRatio { get; set; }

        /// <summary>
        /// <see cref="DungeonHealthCurve"/>'s target for the gem's level - the NUMERATOR of
        /// <see cref="HealthNormalizeRatio"/> and the basis of <see cref="TrashHealthFloor"/>. 0 when the
        /// master switch is off, the same "no curve" convention the curve itself uses for a non-positive level.
        ///
        /// Carried rather than recomputed, on the same reasoning as <see cref="LootQuantityMult"/>: the
        /// spawner's plan log line has to print it next to the ratio it produced, and a second
        /// DungeonHealthCurve.Target call there would be a second place the same arguments are assembled. It is
        /// pure, so there is no drift risk either way - this is simply the plan already knowing the answer.
        /// Nothing derives behaviour from it; the ratio and the floor are what the spawner applies.
        /// </summary>
        public double HealthCurveTarget { get; set; }

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
        /// The run's stat curve (<see cref="DungeonStatCurve.Anchored"/>), or null when the curve does not apply:
        /// dynamic_dungeons_stat_curve off, the run at or below the pivot, or no profile delegate. When set,
        /// <see cref="BandStandard"/> IS its standard at the run level, and the spawner asks it for a stamped
        /// creature's scaling ratios and for the standard at that creature's own level (the #1173 defense cap).
        /// </summary>
        public DungeonStatCurve.Anchored StatCurve { get; set; }

        /// <summary>
        /// The reach-up pivot P = floor(authored top / band high) this plan was built against (326 on the shipped
        /// roster), or 0 when neither reach-up nor the stat curve is on. Carried for the log line and tests.
        /// </summary>
        public int ReachUpPivot { get; set; }

        /// <summary>
        /// The reach-up level scale k = L / P actually applied, or 1.0 (no projection): at or below the pivot,
        /// with dynamic_dungeons_reach_up off, or when no pivot could be measured. Carried for the log line and
        /// tests; every projected read in the builder used exactly this number.
        /// </summary>
        public double ReachUpScale { get; set; } = 1.0;

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

        // ---- Group Threads (Docs/Threads/GROUP-THREADS-DESIGN.md sections 5, 6.1, 6.2) ----------------------
        // Every one of these holds its neutral value on a solo plan: the builder writes them only when
        // DungeonPopulationLimits.Group.IsGroup, and XpFor/LuminanceFor apply the reward factors only when
        // GroupRosterSize > 1. The factors themselves are GroupScaling's; nothing here re-derives a formula.

        /// <summary>N, the locked roster size. 1 on a solo plan.</summary>
        public int GroupRosterSize { get; set; } = 1;

        /// <summary>C, the target count multiplier before the group cap.</summary>
        public double GroupCountTarget { get; set; } = 1.0;

        /// <summary>C_actual = groupSlots / soloSlots, after the cap and the never-below-solo rule (R27).</summary>
        public double GroupCountActual { get; set; } = 1.0;

        /// <summary>H = E / C_actual. Already folded into <see cref="HealthMultiplier"/>; carried for the run and the log.</summary>
        public double GroupHealthMult { get; set; } = 1.0;

        /// <summary>
        /// Damage rating added to trash AND boss at stamp time (ThreadDungeonSpawner.StampedDamageRating),
        /// deliberately NOT folded into <see cref="DamageRating"/> or <see cref="BossDamageRating"/>: the boss
        /// floor is max(mods, DamageRating + floor), so a bonus inside DamageRating would be counted twice and a
        /// bonus inside BossDamageRating could be swallowed by the floor (ruling R15).
        /// </summary>
        public int GroupDamageRatingBonus { get; set; }

        /// <summary>Per-kill XP and luminance factor for trash and elites: H * B / T. 1.0 on a solo plan.</summary>
        public double GroupTrashRewardFactor { get; set; } = 1.0;

        /// <summary>Per-kill XP and luminance factor for the boss: E * B / T. 1.0 on a solo plan.</summary>
        public double GroupBossRewardFactor { get; set; } = 1.0;

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
        /// ONE field for the whole run rather than a boss/non-boss pair, matching CritRating's own precedent
        /// (DamageRating and DamageResistRating DO now split into a Boss* field, see BossDamageRating /
        /// BossDamageResistRating above): the shipped shield_hollow row is target "monster", so it already
        /// applies to every creature including the boss, and no bosses.json row names it. A target "boss" row
        /// on this kind has nowhere to land today - ThreadDungeonStore rejects it at load time - precisely
        /// because splitting it would add a second field nothing writes differently yet.
        /// </summary>
        public double IgnoreShield { get; set; }

        /// <summary>
        /// The fraction of magic armor and magic resistance every run creature ignores, from
        /// <see cref="DungeonRewardMath.HollowIntensity"/>, in [0, 1]. 0 leaves the axis untouched. One field
        /// for the whole run, on the same reasoning as <see cref="IgnoreShield"/>.
        /// </summary>
        public double HollowIntensity { get; set; }
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

        // ---- boss normalization and the combat-trait strip, carried from the tunables ----------------------
        // The spawner applies these; carrying them keeps it off PropertyManager on the placement path, on the
        // same contract as BossHealthFloorRatio and UpliftRename above.

        /// <summary>DungeonPopulationLimits.BossNormalize, as it applied to THIS plan. False reproduces the legacy boss.</summary>
        public bool BossNormalize { get; set; }

        /// <summary>
        /// The largest NON-BOSS base health - normalized onto the curve and floored, BEFORE the gem's health
        /// multiplier - over EVERY wcid in the run's resolved trash and elite pools, not only the drawn ones, so
        /// the boss is a function of (gem, family, dungeon) and never of the random draw. 0 with no family or
        /// empty pools, and whenever normalization is off.
        ///
        /// Read through the builder's healthOf delegate (ThreadDungeonSpawner.LiveHealthOf). That is a LOWER
        /// BOUND on the spawned Health.MaxValue by at most 1 hp: LiveHealthOf floors Endurance/2 while
        /// AttributeFormula rounds it half away from zero, so an odd authored Endurance spawns 1 hp higher
        /// (measured 2026-09-13: 167 of 1694 roster weenies author an odd Endurance, none carry Endurance CP
        /// ranks). The pack margin swamps that, and the spawner's observed-pack guard closes it exactly.
        /// </summary>
        public uint PoolMaxBase { get; set; }

        /// <summary>R x T(gem): the band term of the boss's base health. 0 when R is 0 or the curve is off.</summary>
        public double BossHealthBandTerm { get; set; }

        /// <summary>M x <see cref="PoolMaxBase"/>: the pack term of the boss's base health. 0 when M is 0.</summary>
        public double BossHealthPackTerm { get; set; }

        /// <summary>
        /// max(<see cref="BossHealthBandTerm"/>, <see cref="BossHealthPackTerm"/>): what the boss's health is
        /// before its own modifiers. 0 means the normalized formula had nothing to say (both terms 0), and the
        /// spawner falls back to the legacy BossHealthTarget for this boss.
        /// </summary>
        public double BossHealthBase { get; set; }

        public double BossOffenseBandRatio { get; set; } = DungeonPopulationLimits.DefaultBossOffenseBandRatio;
        public double BossDefenseBandRatio { get; set; } = DungeonPopulationLimits.DefaultBossDefenseBandRatio;

        /// <summary>DungeonPopulationLimits.StripCombatTraits, as it applied to this plan.</summary>
        public bool StripCombatTraits { get; set; }

        public double CategoryResistFloor { get; set; } = DungeonPopulationLimits.DefaultCategoryResistFloor;
        public double CategoryArmorModCeiling { get; set; } = DungeonPopulationLimits.DefaultCategoryArmorModCeiling;
        public double DefenseSkillCapOffset { get; set; } = DungeonPopulationLimits.DefaultDefenseSkillCapOffset;
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
        /// Ceiling on <see cref="DungeonSpawnPlan.HealthNormalizeRatio"/>. A GUARD against degenerate data or
        /// a fat-fingered anchor, explicitly NOT a design dial and deliberately not a tunable.
        ///
        /// Across the range that matters it is nowhere near binding: measured against the local ace_world on
        /// 2026-09-12, the ratio runs 0.206 to 1.0 over the fourteen Raw Fragment rungs and never exceeds 1.0
        /// at ANY gem level of 20 or above. It exists because the ratio is a QUOTIENT and the bottom of the
        /// ladder is data-poor: at exactly three levels in the whole 1-375 range - 3, 4 and 6 - the band's only
        /// usable sample is a weenie reporting 1 hp, and the unclamped ratio there is about 68x to 71x. Those
        /// are admin-issued gem levels nobody plays, which is precisely why the failure would otherwise be
        /// silent, and why the clamp is a hard constant rather than something an admin has to know to set.
        ///
        /// 25x is chosen to sit an order of magnitude above the worst real ratio seen at a playable level and
        /// still well under the degenerate ones.
        ///
        /// The second half of the same guard is DungeonHealthCurve.MaxAnchorLow/MaxAnchorHigh, which bound the
        /// numerator. Both are needed: the ceilings cannot see a tiny denominator, and this clamp cannot tell
        /// a deliberate anchor from a typo.
        /// </summary>
        public const double MaxHealthNormalizeRatio = 25.0;

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
        /// <param name="memo">
        /// The family-independent memo (see <see cref="ThreadPlanCache"/>). Optional and trailing, exactly as
        /// <paramref name="profileOf"/> and <paramref name="fitsDungeon"/> were added before it, so every call
        /// site that predates the cache keeps compiling; NULL MEANS NO CACHE and is byte-identical to the
        /// pre-cache behaviour, because every use below is a straight either/or against the pure function the
        /// memo wraps.
        ///
        /// It can only ever change how LONG the build takes, never what it produces: nothing it holds depends
        /// on the gem's seed, its drawn family, its pools, its picks or its boss, and every value it hands back
        /// is immutable. That equality is pinned by test (ThreadPlanCacheTests), not by this paragraph.
        /// </param>
        public static DungeonSpawnPlan Build(DungeonGemSpec spec, DungeonEntryDef dungeon, ThreadDungeonStore store,
            IReadOnlyDictionary<string, SpeciesTableDef> species, Func<uint, int> levelOf, Func<uint, uint> healthOf,
            DungeonPopulationLimits limits, Random rng, Func<uint, DungeonStatProfile> profileOf = null,
            Func<uint, bool> fitsDungeon = null, ThreadPlanMemo memo = null)
        {
            var plan = new DungeonSpawnPlan();
            var mods = store.Modifiers;

            // Group Threads. Solo for every caller that predates groups; every use below is guarded on IsGroup.
            var group = limits.Group;

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

            // ---- reach-up: the pivot projection (owner ruling 2026-10-08) ----
            // The authored roster stops at AuthoredTop (375 on the shipped tables), so above the pivot
            // P = floor(AuthoredTop / band high) - 326 - a run's natural band [L, 1.15L] holds fewer and fewer
            // authored creatures and from 375 + 1 up it holds none at all. Before reach-up the band then widened
            // DOWN (DungeonRosterSelector's low-edge ladder) and uplifted whatever it reached to exactly L, and
            // the cross-family health median over the empty natural band was 0, which made HealthNormalizeRatio
            // 0 and every run above 375 silently EASIER than a 375 one.
            //
            // Reach-up instead reads every creature at its PROJECTED level round(raw x k), k = L / P. The band
            // test, the family count, the band median, the band standard's sample, the pools, the boss
            // candidate and the pack level all see that one projected number, so a level-L run draws exactly
            // the creatures that fill the pivot's own natural band, spread across [L, 1.15L] the way they were
            // spread across [P, 1.15P] - and each pick is stamped at its projected level (EntryFor, below). It
            // applies from the pivot upward, not only above 375 (owner ruling): a gem fields monsters in
            // [L, ~1.15L] even where no authored content exists.
            //
            // k is 1 at and below the pivot and whenever the switch is off, and projLevelOf is then the
            // caller's own levelOf delegate - not a wrapper that happens to return the same values - so the
            // plan is byte-identical to the builder before reach-up existed. The pivot is measured whenever
            // either reach-up or the stat curve needs it; the stat curve uses the same pivot as its anchor.
            //
            // The WORLD EVENTS paths are untouched: DungeonRosterSelector, BandUplift and
            // DungeonBandStandard.Compute are called with a projected delegate, never changed.
            var pivot = limits.ReachUp || limits.StatCurve
                ? ReachUpPivot(AuthoredTopLevel(species, levelOf), band.High)
                : 0;
            var reachScale = limits.ReachUp ? ReachUpScale(spec.Level, pivot) : 1.0;
            Func<uint, int> projLevelOf = reachScale == 1.0 ? levelOf : w => ProjectLevel(levelOf(w), reachScale);

            plan.ReachUpPivot = pivot;
            plan.ReachUpScale = reachScale;

            // The band standard's sample reads DungeonStatProfile.Level rather than levelOf, so the projection
            // reaches it through the profile: the same rounding applied to the same authored level.
            Func<uint, DungeonStatProfile> projProfileOf = reachScale == 1.0 || profileOf == null
                ? profileOf
                : w =>
                {
                    var profile = profileOf(w);
                    return profile == null ? null : profile.WithLevel(ProjectLevel(profile.Level, reachScale));
                };

            // ---- the five memoisable intermediates (ThreadPlanCache) ----
            // Each local function is the pure call it replaces when there is no memo, so the null path below is
            // literally the pre-cache code and cannot drift from it. Written as local functions rather than as
            // a conditional at each site because three of the five are called more than once, and a memo that
            // some call sites used and others did not would be a silent correctness hazard rather than a
            // performance one - the projection in particular must be the SAME instance everywhere, or the band
            // keys built from it stop matching.
            DungeonFitFilter.FitProjection ProjectSpecies(IReadOnlyDictionary<string, SpeciesTableDef> tables, Func<uint, bool> fits)
                => memo != null ? memo.Project(tables, fits) : DungeonFitFilter.Project(tables, fits);

            // Every one of these reads the PROJECTED level (projLevelOf / projProfileOf) and passes the reach-up
            // scale into the memo's key: the same (tables, level, band) at a different k is a different roster,
            // and a key without k would serve a switched-off run the entries a reach-up run stored, or the
            // reverse, the moment an admin toggled dynamic_dungeons_reach_up.
            DungeonRosterBand EligibilityBandOf(IReadOnlyDictionary<string, SpeciesTableDef> tables, DungeonRosterBand natural)
                => memo != null
                    ? memo.ResolveEligibilityBand(tables, spec.Level, projLevelOf, natural, lowFloor, limits.MinEligibleFamilies, reachScale)
                    : DungeonRosterSelector.ResolveEligibilityBand(tables, spec.Level, projLevelOf, natural, lowFloor, limits.MinEligibleFamilies);

            int EligibleFamiliesAt(IReadOnlyDictionary<string, SpeciesTableDef> tables, DungeonRosterBand at)
                => memo != null
                    ? memo.EligibleFamilyCount(tables, spec.Level, projLevelOf, at, reachScale)
                    : DungeonRosterSelector.EligibleFamilyCount(tables, spec.Level, projLevelOf, at);

            uint BandMedianHealthAt(DungeonRosterBand at)
                => memo != null
                    ? memo.BandMedianHealth(species, spec.Level, projLevelOf, healthOf, at, reachScale)
                    : DungeonRosterSelector.BandMedianHealth(species, spec.Level, projLevelOf, healthOf, at);

            // The measured standard at ANY level and scale - the run's own (projected) one, or the stat curve's
            // anchor at the pivot, where k is 1 by definition.
            DungeonBandStandard MeasuredStandardAt(int level, double scale, Func<uint, DungeonStatProfile> profiles, DungeonRosterBand natural)
                => memo != null
                    ? memo.BandStandard(species, level, profiles, natural, lowFloor, scale)
                    : DungeonBandStandard.Compute(species, level, profiles, natural, lowFloor);

            DungeonBandStandard BandStandardAt(DungeonRosterBand natural)
                => MeasuredStandardAt(spec.Level, reachScale, projProfileOf, natural);

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
                var projection = ProjectSpecies(species, fitsDungeon);

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
                    var resolved = EligibilityBandOf(tables, band);

                    if (!string.IsNullOrEmpty(spec.Family) && spec.Family != DungeonGemSpec.Any)
                    {
                        return tables.TryGetValue(spec.Family, out var forced) && forced != null
                            && DungeonRosterSelector.IsEligible(forced, spec.Level, projLevelOf, resolved) ? 1 : 0;
                    }

                    return EligibleFamiliesAt(tables, resolved);
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

            // The modifier magnitude curve (DungeonModifierLevelScale): s = min(1, (level / 185)^k), computed ONCE
            // from the gem level and passed to every call below that turns a modifier magnitude into a monster
            // stat, a population knob or an XP/luminance factor. 1.0 at and above 185, where every call below is
            // exactly what it was. NOT passed to loot quantity, loot quality or salvage affinity - those already
            // carry the gem-level reward-scale ratio further down.
            var levelScale = DungeonModifierLevelScale.Factor(spec.Level, limits.ModifierLevelExponent);
            plan.ModifierLevelScale = levelScale;

            plan.HealthMultiplier = DungeonRewardMath.HealthMultiplier(spec, mods, Array.Empty<string>(), levelScale: levelScale);

            // The non-boss health terms - the normalization ratio and the floor. Both computed ONCE against
            // the gem's own level, never against the drawn family, which would collapse the denominator back
            // to the family-scoped median the whole point of BandMedianHealth is to avoid.
            //
            // Deliberately against the NATURAL band, not the widened draw band the selection below may end
            // up using. Health is the one axis the uplift does NOT re-do (see DungeonBandStandard's class
            // comment), so these two terms ARE the uplift's health term - and a denominator computed over the
            // widened band would be the median of a pool that includes the very under-levelled creatures the
            // uplift exists to raise, which is a standard that lowers itself the more it is needed.
            var trashRatio = double.IsNaN(limits.TrashHealthFloorRatio) || limits.TrashHealthFloorRatio < 0
                ? 0.0
                : limits.TrashHealthFloorRatio;

            // The BAND STANDARD the run's health is priced against: DungeonHealthCurve's smooth monotone
            // target for the gem's level, or 0 when the master switch is off ("no curve").
            var curveTarget = DungeonHealthCurve.Target(spec.Level, limits);
            var curveOn = limits.HealthCurveEnabled && curveTarget > 0;
            plan.HealthCurveTarget = curveTarget;
            //
            // BandMedianHealth is now the DENOMINATOR rather than the output, and it is read whenever EITHER
            // consumer needs it: the curve needs it to form a ratio even when the floor ratio is 0, and the
            // legacy floor needs it only when the floor ratio is positive. With the curve off and the ratio 0
            // it is not read at all, exactly as before.
            //
            // THE RAW DICTIONARY, NOT speciesForSelection, AND THAT IS DELIBERATE. This is a statistic about
            // the BAND - what a level-N creature in this band typically carries - not about who may be placed
            // in this particular dungeon. Filtering it would make the same gem normalize by two different
            // ratios depending on which dungeon it opened, and would move the denominator by removing the
            // seven tallest (and generally heaviest) creatures from the sample.
            var bandMedian = trashRatio > 0 || curveOn
                ? BandMedianHealthAt(band)
                : 0;

            if (curveOn)
            {
                // No denominator means nothing to normalize AGAINST - but the curve still states a floor, and
                // a floor needs no sample. The two are independent for that reason.
                plan.HealthNormalizeRatio = bandMedian > 0
                    ? Math.Clamp(curveTarget / bandMedian, 0.0, MaxHealthNormalizeRatio)
                    : 0.0;

                // The floor is now half (trashRatio) of the band standard measured on the CURVE rather than on
                // the sample, so dynamic_dungeons_trash_health_floor_ratio keeps its meaning exactly and still
                // catches an under-levelled draw from a widened band - which the proportional normalization
                // alone cannot, since scaling a creature relative to the sample leaves a creature far below
                // the sample still far below it.
                plan.TrashHealthFloor = (uint)Math.Clamp(Math.Round(curveTarget * trashRatio), 0, int.MaxValue);
            }
            else
            {
                plan.HealthNormalizeRatio = 0.0;
                plan.TrashHealthFloor = (uint)Math.Clamp(Math.Round(bandMedian * trashRatio), 0, int.MaxValue);
            }

            plan.DamageRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.DamageRating, levelScale: levelScale);
            plan.CritRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.CritRating, levelScale: levelScale);
            plan.CritDamageRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.CritDamageRating, levelScale: levelScale);
            plan.DamageResistRating = DungeonRewardMath.RatingTotal(spec, mods, Array.Empty<string>(), DungeonRewardMath.DamageResistRating, levelScale: levelScale);
            plan.RunSpeedMult = DungeonRewardMath.RunValue(spec, mods, DungeonRewardMath.RunSpeedMult, 1.0, levelScale);
            plan.IgnoreShield = DungeonRewardMath.IgnoreShieldFraction(spec, mods, Array.Empty<string>(), levelScale: levelScale);
            plan.HollowIntensity = DungeonRewardMath.HollowIntensity(spec, mods, Array.Empty<string>(), levelScale: levelScale);
            // dynamic_dungeons_modifier_reward_scale (owner ruling): shrinks ONLY the bonus above neutral of
            // these three capped products - effective = 1 + s * (product - 1), applied AFTER the cap so every
            // gem pays the same proportional cut of its own capped bonus. 1.0 is a no-op. See
            // DungeonPopulationLimits.ModifierRewardScale for the full contract, including why this is a
            // DIFFERENT scalar from the gem-level reward-scale ratio below (which is level-keyed and untouched
            // by this dial).
            plan.XpMultiplier = DungeonRewardMath.ApplyModifierRewardScale(limits.ModifierRewardScale,
                DungeonRewardMath.XpMultiplier(spec, mods, limits.XpCap, levelScale));
            plan.BossXpMultiplier = DungeonRewardMath.ApplyModifierRewardScale(limits.ModifierRewardScale,
                DungeonRewardMath.BossXpMultiplier(spec, mods, limits.XpCap, levelScale));
            plan.LumMultiplier = DungeonRewardMath.ApplyModifierRewardScale(limits.ModifierRewardScale,
                DungeonRewardMath.LumMultiplier(spec, mods, limits.LumCap, levelScale));

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

            // Salvage affinity's OWN ratio (owner ruling): the SAME anchor/exponent/floor as XP/luminance/
            // loot-quantity above, but with the cap argument forced to 0 (uncapped) regardless of
            // limits.RewardScaleCap - affinity keeps growing with gem level past the anchor no matter what
            // the other three axes are capped at. dynamic_dungeons_salvage_affinity_chance_cap
            // (limits.SalvageAffinityChanceCap) is the one and only ceiling on the RESULT, applied below where
            // the chance itself is clamped. dynamic_dungeons_modifier_reward_scale never reaches this ratio or
            // this clamp - salvage affinity is untouched by that dial, by design.
            var affinityRewardScaleRatio = limits.RewardScalingEnabled
                ? DungeonRewardMath.RewardScaleRatio(spec.Level, limits.RewardScaleAnchor, limits.RewardScaleExponent,
                    limits.RewardScaleFloor, 0.0)
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
            // ADDITIVE axis (neutral value 0): effective = ratio * raw, clamped to
            // [0, limits.SalvageAffinityChanceCap] (never the shared [0, 1] every other additive axis on this
            // struct uses) since dynamic_dungeons_salvage_affinity_chance_cap is this axis's own, separate
            // ceiling on top of its own, separate (uncapped) ratio - see affinityRewardScaleRatio above.
            plan.SalvageAffinities = DungeonRewardMath.SalvageAffinities(spec, mods)
                .Select(a => (a.MaterialId, a.BaseWcid, Chance: Math.Clamp(DungeonRewardMath.ApplyAdditiveScale(affinityRewardScaleRatio, a.Chance), 0.0, limits.SalvageAffinityChanceCap)))
                .ToList();
            // Loot QUANTITY is multiplicative (neutral 1.0, a count multiplier); loot QUALITY is additive
            // (neutral 0.0, a bonus added to LootQualityMod). LootQuantityMultiplier is already clamped to
            // [1, limits.LootQuantityCap] before either scale is applied, on the same "cap bounds the gem's own
            // product, the scale sits outside it" reasoning as XpScale/LumScale above.
            //
            // TWO scales compose here, in this order: dynamic_dungeons_modifier_reward_scale first (shrinks
            // the capped GEM product toward neutral), then the level-keyed reward-scale ratio second (scales
            // the result the same way it scales XpScale/LumScale above). Order matters only in that both are
            // ApplyMultiplicativeScale around a shared pivot of 1.0, so composing them either order reaches the
            // same 1.0 no-op when either is neutral; this order matches "the modifier's own bonus is cut first,
            // then the uniform server-wide curve applies to what remains" (DungeonPopulationLimits.ModifierRewardScale).
            var cappedLootQuantityMult = DungeonRewardMath.LootQuantityMultiplier(spec, mods, limits.LootQuantityCap);
            var modifierScaledLootQuantityMult = DungeonRewardMath.ApplyModifierRewardScale(limits.ModifierRewardScale, cappedLootQuantityMult);
            var scaledLootQuantityMult = DungeonRewardMath.ApplyMultiplicativeScale(rewardScaleRatio, modifierScaledLootQuantityMult);
            var scaledLootQualityBonus = DungeonRewardMath.ApplyAdditiveScale(rewardScaleRatio, DungeonRewardMath.LootQualityBonus(spec, mods));
            plan.Profile = DungeonRewardMath.BuildProfile(spec.Tier, scaledLootQualityBonus, limits.LootQualityCap, limits.LootTierCap,
                scaledLootQuantityMult);
            // Carried alongside the profile rather than discarded, so the boss cache can size its own roll
            // from the SAME multiplier the run's creatures were built with. BuildProfile has already folded
            // it into the profile's maximum item counts; the cache needs the scalar itself, because it rolls
            // a fixed COUNT of items rather than one profile draw.
            plan.LootQuantityMult = scaledLootQuantityMult;

            // The uplift and reach-up tags, in ONE place so every construction site of a DungeonSpawnPlanEntry
            // below gets the identical rule:
            //   - a pick whose (projected) level sits below the natural band's low edge could only have been
            //     reached by the extension, and carries the GEM's level as its normalization target - exactly
            //     the pre-reach-up uplift rule, since the projected level IS the live level whenever k is 1;
            //   - otherwise a pick whose projected level is above its authored one is STAMPED at the projected
            //     level (DungeonSpawnPlanEntry.StampLevel) - which only happens above the pivot with reach-up on;
            //   - everything else carries neither and is untouched by both features.
            // When nothing widened, no pick can be below that edge by construction, so an unwidened run at or
            // below the pivot tags nothing at all.
            DungeonSpawnPlanEntry EntryFor(uint wcid, DungeonRole role, DungeonSpawnPointDef point)
            {
                var projected = projLevelOf(wcid);

                if (projected < plan.NaturalBandLow)
                    return new DungeonSpawnPlanEntry(wcid, role, point, spec.Level);

                var raw = levelOf(wcid);

                return new DungeonSpawnPlanEntry(wcid, role, point, 0, projected > raw ? projected : 0);
            }

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
            var eligibilityBand = EligibilityBandOf(speciesForSelection, band);

            if (eligibilityBand.Low < band.Low)
                plan.Notes.Add($"band: only {EligibleFamiliesAt(speciesForSelection, band)} " +
                               $"family/families in the natural band; low edge widened from x{band.Low:0.##} to x{eligibilityBand.Low:0.##} " +
                               $"(want {limits.MinEligibleFamilies})");

            try
            {
                family = DungeonRosterSelector.PickFamily(speciesForSelection, spec.Family, dungeon.Families, dungeon.CreatureTypes, spec.Level, projLevelOf, rng, eligibilityBand);
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
                trashBand = DungeonRosterSelector.ResolveFamilyTrashBand(family, spec.Level, projLevelOf, eligibilityBand, lowFloor, limits.MinFamilyPool);
                eliteBand = DungeonRosterSelector.ResolveFamilyEliteBand(family, spec.Level, projLevelOf, eligibilityBand, lowFloor);

                trashPool = DungeonRosterSelector.PoolFor(family, spec.Level, DungeonRole.Trash, projLevelOf, trashBand);

                // ElitePoolFor, NOT PoolFor: the empty-elite fallback has to land on the TRASH band's pool,
                // and PoolFor would take it against the ELITE band's trash members instead - which for a
                // family with no elite at any width is the widest band the floor allows, so the elite slots
                // would draw from a deeper pool than the trash slots in the same run. Every consumer below
                // spells the fallback out against trashPool for that reason.
                elitePool = DungeonRosterSelector.ElitePoolFor(family, spec.Level, projLevelOf, eliteBand);

                plan.EffectiveBandLow = DungeonRosterSelector.TrashBand(spec.Level, trashBand).Low;

                if (trashBand.Low < eligibilityBand.Low)
                    plan.Notes.Add($"band: family {family.Id} had {DungeonRosterSelector.PoolFor(family, spec.Level, DungeonRole.Trash, projLevelOf, eligibilityBand).Count} " +
                                   $"distinct trash wcid(s) at x{eligibilityBand.Low:0.##}; low edge widened to x{trashBand.Low:0.##} " +
                                   $"(want {limits.MinFamilyPool}, got {trashPool.Count})");

                var points = dungeon.Points.Where(p => p != null && p.Curated && !IsAnchor(p, dungeon.BossAnchor)).ToList();
                var countMult = DungeonRewardMath.RunValue(spec, mods, DungeonRewardMath.CountMult, 1.0, levelScale);
                if (double.IsNaN(countMult) || countMult < 0) countMult = 1.0;

                // count_mult and MinMonsters can both ADD creatures beyond the curated point count, by
                // sharing points round-robin below - but neither ever invents a spawn LOCATION the curator
                // did not place; a dungeon with zero curated points still gets zero slots. desired starts
                // from count_mult, MinMonsters raises it as a floor, MaxMonsters then caps it as the hard
                // ceiling (a misconfigured minimum above the max is silently bounded by the max, never the
                // other way around).
                var desired = (int)Math.Round(points.Count * countMult);
                var slots = Math.Max(desired, limits.MinMonsters);
                slots = Math.Min(slots, limits.MaxMonsters);
                slots = Math.Max(0, slots);
                if (points.Count == 0)
                    slots = 0;

                // Group Threads count (spec 6.1). The solo arithmetic above is untouched and its result is the
                // group count's lower bound (R27): a group cap below the solo count can never lower the count
                // and so inflate H. The group count answers to the GROUP cap, not MaxMonsters. Solo takes no
                // part of this block, so its slot count and its rng draws are exactly what they were.
                if (group.IsGroup)
                {
                    var soloSlots = slots;
                    slots = GroupScaling.GroupSlotsFor(points.Count, countMult, limits.MinMonsters, group.CountTarget, group.GroupMaxMonsters, soloSlots);

                    // C_actual never exceeds E (fix round 1). The group count answers to the group cap, not
                    // MaxMonsters, so a solo count capped low by MaxMonsters could otherwise leave C_actual above
                    // E and H = E / C_actual below 1.0 - group creatures weaker than solo ones. Capping the count
                    // at floor(soloSlots x E) keeps H >= 1; never below soloSlots (R27). Saturates at int range.
                    if (soloSlots > 0)
                        slots = Math.Min(slots, Math.Max(soloSlots, (int)Math.Min(Math.Floor(soloSlots * group.Effort), int.MaxValue)));

                    plan.GroupCountActual = GroupScaling.CountActualFor(soloSlots, slots);
                }

                if (eliteBand.Low < eligibilityBand.Low)
                    plan.Notes.Add($"band: family {family.Id} had no elite in the x{eligibilityBand.Low:0.##} band; " +
                                   $"elite low edge widened to x{eliteBand.Low:0.##} ({elitePool.Count} in pool)");

                var eliteShare = DungeonRewardMath.RunValue(spec, mods, DungeonRewardMath.EliteShare, DefaultEliteShare, levelScale);
                var picks = DungeonRosterSelector.PickPopulation(trashPool, elitePool, slots, eliteShare, rng);

                // Shuffle the points so a count_mult below 1, or a MaxMonsters cap, does not always drop the
                // same (deepest) rooms - the curated list comes out of the tool in BFS order.
                var shuffled = points.OrderBy(_ => rng.Next()).ToList();

                // The first min(slots, shuffled.Count) picks go to distinct points exactly as before. Any
                // overflow (count_mult > 1, or MinMonsters exceeding the curated point count) wraps
                // round-robin over the SAME shuffled order, so no point takes a second creature until every
                // point has one, and point loads differ by at most 1.
                for (var i = 0; i < picks.Count && shuffled.Count > 0; i++)
                    plan.Entries.Add(EntryFor(picks[i].Wcid, picks[i].Role, shuffled[i % shuffled.Count]));

                if (picks.Count > shuffled.Count && shuffled.Count > 0)
                {
                    var maxPerPoint = (picks.Count + shuffled.Count - 1) / shuffled.Count;
                    plan.Notes.Add($"population: {picks.Count} creature(s) shared across {shuffled.Count} curated point(s) " +
                                   $"(up to {maxPerPoint} per point)");
                }

                if (picks.Count == 0)
                    plan.Notes.Add($"family {family.Id}: population came back empty");
            }

            // ---- boss normalization and trait-strip dials, carried to the spawner ----
            // Garbage can only reach here from a limits object built in code (the spawner's readers sanitize the
            // live tunables); a NaN or negative ratio reads as 0, the documented "leave that axis authored".
            plan.BossNormalize = limits.BossNormalize;
            plan.BossOffenseBandRatio = NonNegativeOrZero(limits.BossOffenseBandRatio);
            plan.BossDefenseBandRatio = NonNegativeOrZero(limits.BossDefenseBandRatio);
            plan.StripCombatTraits = limits.StripCombatTraits;
            plan.CategoryResistFloor = NonNegativeOrZero(limits.CategoryResistFloor);
            plan.CategoryArmorModCeiling = NonNegativeOrZero(limits.CategoryArmorModCeiling);
            // NOT NonNegativeOrZero: a negative offset is this axis's OWN "disable" value (0 is a valid cap,
            // exactly at the band's effective median), so only a negative value reads that way. NaN/Infinity is
            // garbage, not a deliberate "disable" - it reads as the compiled default, the same contract
            // SanitizeDefenseSkillCapOffset and the PropertyManager registration both document for this dial.
            plan.DefenseSkillCapOffset = double.IsNaN(limits.DefenseSkillCapOffset) || double.IsInfinity(limits.DefenseSkillCapOffset)
                ? DungeonPopulationLimits.DefaultDefenseSkillCapOffset
                : limits.DefenseSkillCapOffset;

            // PoolMaxBase: the toughest non-boss base over the WHOLE resolved pools the draw used, not over the
            // drawn entries, so the normalized boss is deterministic per (gem, family, dungeon). The per-wcid base
            // is ThreadDungeonSpawner.NonBossHealthTarget itself at a multiplier of 1.0 - the exact arithmetic the
            // spawner applies to a non-boss, not a restatement of it - so "normalized, floored, before hpMult"
            // cannot drift from what the pack really gets.
            if (plan.BossNormalize && family != null)
            {
                foreach (var wcid in trashPool.Concat(elitePool).Distinct())
                {
                    var nonBossBase = (uint)ThreadDungeonSpawner.NonBossHealthTarget(healthOf(wcid), plan.HealthNormalizeRatio, 1.0, plan.TrashHealthFloor);

                    if (nonBossBase > plan.PoolMaxBase)
                        plan.PoolMaxBase = nonBossBase;
                }
            }

            // ---- boss ----
            var bossLow = spec.Level * BossLevelWindowLow;
            var bossHigh = spec.Level * BossLevelWindowHigh;

            // Any-family rows at every band (dynamic_dungeons_boss_any_all_bands, only while normalizing): a
            // normalized boss is re-priced onto the gem's band regardless of its authored level, so the window
            // no longer protects anything for a row every family may use. FAMILY rows keep the window - their
            // level is part of what makes them that family's headline.
            var anyRowsAllBands = plan.BossNormalize && limits.BossAnyAllBands;

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
                .Where(b => (anyRowsAllBands && (b.Families?.Count ?? 0) == 0) || (b.Level >= bossLow && b.Level <= bossHigh))
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
                var boss = PickBossRow(bosses, plan.FamilyId, limits.BossFamilyRowWeight, rng);
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
                    projLevelOf, rng, drawn);

                if (candidate != 0)
                {
                    plan.BossWcid = candidate;
                    bossModifiers = PromotedBossModifiers;
                    promoted = true;
                    plan.Notes.Add($"boss: no curated row eligible for level {spec.Level}; promoted wcid {candidate} (level {projLevelOf(candidate)}) from family {family.Id}");
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
                plan.BossHealthMultiplier = DungeonRewardMath.HealthMultiplier(spec, mods, bossModifiers, forBoss: true, levelScale: levelScale);

                // HealthMultiplier(forBoss: true) folds every gem modifier whose Target is "monster" (the default)
                // plus every boss-targeted one, on top of the boss row's own extra modifiers, each at MinMagnitude.
                // The pack figure above (plan.HealthMultiplier, forBoss left false) excludes boss-targeted gem
                // modifiers, so a gem's boss-only effect (e.g. boss_guarded) lands on the boss alone and never
                // leaks onto the pack. So boss >= pack holds exactly when no extra boss health_mult row authors a
                // magnitude below 1.0 - true of the shipped modifiers.json (boss_guarded 1.5) and pinned by a test
                // over the shipped file. Clamped anyway while normalizing, because "boss outranks its pack" must
                // survive a content edit.
                if (plan.BossNormalize)
                    plan.BossHealthMultiplier = Math.Max(plan.BossHealthMultiplier, plan.HealthMultiplier);

                plan.BossDamageRating = DungeonRewardMath.RatingTotal(spec, mods, bossModifiers, DungeonRewardMath.DamageRating, forBoss: true, levelScale: levelScale);
                plan.BossDamageResistRating = DungeonRewardMath.RatingTotal(spec, mods, bossModifiers, DungeonRewardMath.DamageResistRating, forBoss: true, levelScale: levelScale);

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
                        plan.Entries[i] = EntryFor(replacement, e.Role, e.Point);
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
                // The pack term reads the PROJECTED level, which for a reach-up pick is exactly its StampLevel,
                // so the boss outranks every stamp in its own room as well as every authored level: a 375 gem
                // whose pack stamps up to 431 stamps its boss at 432, not at ceil(375 x 1.10) = 413.
                var packLevel = plan.Entries.Where(e => e.Role != DungeonRole.Boss).Select(e => projLevelOf(e.Wcid)).DefaultIfEmpty(0).Max();
                var margin = double.IsNaN(limits.BossLevelMargin) || limits.BossLevelMargin < 1.0 ? 1.0 : limits.BossLevelMargin;

                // While NORMALIZING the first term is dropped: the boss's stats are re-set onto the gem's band,
                // so an authored level far above that band (a level-500 any-family row at a 185 gem) would stamp
                // a number the creature no longer fights like, and price its XP off it. The stamp becomes
                // max(pack + 1, gem + 1, ceil(gem x margin)).
                var bossLevel = plan.BossNormalize ? 0.0 : (double)levelOf(plan.BossWcid);
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

                // The NORMALIZED boss's base health: max(R x T(gem), M x PoolMaxBase), both computed here so the
                // whole thing is logged and unit-tested before anything spawns. The spawner multiplies by
                // BossHealthMultiplier and applies DungeonRewardMath.NormalizedBossHealthTarget's observed-pack
                // guard. A base of 0 (both dials 0, or no curve and no pools) sends the spawner down the legacy
                // BossHealthTarget path rather than spawning a 1-hp boss.
                if (plan.BossNormalize)
                {
                    plan.BossHealthBandTerm = DungeonRewardMath.BossHealthBandTerm(curveTarget, limits.BossHealthBandRatio);
                    plan.BossHealthPackTerm = DungeonRewardMath.BossHealthPackTerm(plan.PoolMaxBase, limits.BossHealthPackMargin);
                    plan.BossHealthBase = Math.Max(plan.BossHealthBandTerm, plan.BossHealthPackTerm);
                }

                // I6: the boss entry is the LAST element of Entries, and that is load-bearing for I2 - it is
                // what makes the pack's observed health maximum known by the time the spawner places the boss.
                //
                // The boss entry carries the uplift tag on the same rule as every other entry (I-G). A
                // family-bound CURATED boss is drawn inside a level window that starts at 1.0x the gem level,
                // so it can never be tagged; a PROMOTED one comes out of the family's own pool and can be.
                // When it is, the spawner applies the band normalization AND the boss uplift, in that order,
                // and neither can lower the other because both are floors.
                //
                // ONLY a promoted boss is ever tagged. An ANY-family curated row fielded outside its window by
                // dynamic_dungeons_boss_any_all_bands (2026-09-13) can sit below the natural band, and tagging it
                // off EntryFor would switch the uplift on for that one entry even with the band low floor at 1.0,
                // breaking I-E. It needs no uplift anyway: that switch only acts while boss normalization is on,
                // and normalization SETS the boss to the band standard.
                plan.Entries.Add(promoted
                    ? EntryFor(plan.BossWcid, DungeonRole.Boss, dungeon.BossAnchor)
                    : new DungeonSpawnPlanEntry(plan.BossWcid, DungeonRole.Boss, dungeon.BossAnchor));

                if (promoted)
                    plan.Notes.Add($"boss: promoted wcid {plan.BossWcid} stamped to level {plan.BossLevel} (pack max {packLevel}, gem {spec.Level})");
            }

            // ---- band standard ----
            // Resolved LAST and only when something was actually tagged: the sample walks every role-0 member
            // of every table and builds a profile for each, which is far more work than the two scalar reads
            // the rest of the plan needs, and a run that never widened its band must not pay for it. This is
            // also what keeps I-A and I-E cheap as well as correct - with the feature off the delegate is
            // never invoked at all.
            // Two more consumers since 2026-09-13, each of which needs the standard on a run that widened
            // nothing: boss normalization SETS the boss's offense and defense to it, and the category guard caps
            // every non-boss defense skill at a multiple of its medians. The cost argument above still holds for
            // a run needing neither, which is only a run with both switches off.
            var anyUplift = plan.Entries.Any(e => e.UpliftLevel > 0);
            var standardForBoss = plan.BossNormalize && plan.BossWcid != 0;
            var standardForDefenseCap = plan.StripCombatTraits && plan.DefenseSkillCapOffset >= 0 && plan.Entries.Any(e => e.Role != DungeonRole.Boss);

            // The stat curve (owner ruling 2026-10-08) applies strictly ABOVE the pivot and only while its switch
            // is on. Independent of the reach-up switch on purpose: a run above the pivot with reach-up off still
            // normalizes its uplifted entries and its boss to the curve, rather than to whatever the widened
            // sample happens to hold. A STAMPED entry is a fourth consumer - its own scaling ratios come off the
            // curve - so a run with stamps needs the curve even when none of the three above does.
            var curveApplies = limits.StatCurve && pivot > 0 && spec.Level > pivot;
            var standardForStamps = curveApplies && plan.Entries.Any(e => e.StampLevel > 0);

            if (profileOf != null && (anyUplift || standardForBoss || standardForDefenseCap || standardForStamps))
            {
                // THE RAW DICTIONARY, for the same reason BandMedianHealth uses it: the band standard is what
                // a level-N creature in this band typically carries, a property of the BAND and not of who can
                // be placed in this dungeon. A dungeon-dependent standard would normalize the identical
                // uplifted creature to two different stat lines depending on where it spawned.
                //
                // ABOVE THE PIVOT, WITH THE CURVE ON, the standard is the curve's: the measured standard AT THE
                // PIVOT (its natural band, unprojected - k is 1 there by definition, and that band is the last
                // one wholly inside the authored data) carried up to the run level by DungeonStatCurve's fitted
                // slopes. It is derived here from the real band standard every time, never from a hardcoded
                // anchor, so a roster edit moves the anchor with it. Otherwise it is the run's own measured
                // standard, over the PROJECTED sample when reach-up is on - which above the pivot is the pivot's
                // own sample again, i.e. the flat standard the curve exists to replace.
                if (curveApplies)
                {
                    plan.StatCurve = new DungeonStatCurve.Anchored(MeasuredStandardAt(pivot, 1.0, profileOf, band), pivot, limits.DefenseCurveRateAbove375);
                    plan.BandStandard = plan.StatCurve.StandardAt(spec.Level);
                }
                else
                {
                    plan.BandStandard = BandStandardAt(band);
                }

                // The uplift note reports the UPLIFT, so it is written only when something was tagged; a standard
                // computed purely for the boss or the defense cap must not claim "0 entry/entries normalized".
                if (anyUplift)
                    plan.Notes.Add(plan.BandStandard.IsEmpty
                        ? $"uplift: {plan.Entries.Count(e => e.UpliftLevel > 0)} entry/entries tagged for level {spec.Level} but the band sample had no usable data; only the level is normalized"
                        : $"uplift: {plan.Entries.Count(e => e.UpliftLevel > 0)} entry/entries normalized to level {spec.Level} against a {plan.BandStandard.SampleCount}-member band standard " +
                          $"(sample low x{plan.BandStandard.SampleLowRatio:0.##}, dmg {plan.BandStandard.MaxBodyDamage}, armor {plan.BandStandard.MaxBaseArmor}, {plan.BandStandard.SkillMedians.Count} skill(s))");
            }

            // ---- Group Threads health, damage and reward factors (spec 6.1, 6.2) ----
            // LAST, after the boss clamp and every other health term, so H multiplies the finished pack
            // multiplier (the spawner applies it after the curve, the normalization and the trash floor) and E
            // the finished boss multiplier. Boss >= pack still holds: C_actual >= 1 (R27), so H <= E.
            // PoolMaxBase and the boss base terms are measured at multiplier 1.0 and are deliberately untouched.
            if (group.IsGroup)
            {
                plan.GroupRosterSize = group.RosterSize;
                plan.GroupCountTarget = group.CountTarget;
                plan.GroupHealthMult = GroupScaling.HealthMultFor(group.Effort, plan.GroupCountActual);
                plan.HealthMultiplier *= plan.GroupHealthMult;

                if (plan.BossWcid != 0)
                    plan.BossHealthMultiplier *= group.Effort;

                plan.GroupDamageRatingBonus = group.DamageRatingBonus;
                plan.GroupTrashRewardFactor = group.TrashRewardFactor(plan.GroupHealthMult);
                plan.GroupBossRewardFactor = group.BossRewardFactor();
            }

            return plan;
        }

        /// <summary>
        /// Draws one curated boss row from the eligible shortlist (dynamic_dungeons_boss_family_row_weight).
        ///
        /// A row whose families list names <paramref name="familyId"/> weighs <paramref name="familyRowWeight"/>;
        /// every other eligible row (the any-family ones) weighs 1. The draw walks the rows in WCID order - ties
        /// keep their file order, since OrderBy is stable - and takes ONE rng.NextDouble(), so a gem seed always
        /// reproduces its boss and adding a bosses.json row anywhere in the file cannot reshuffle which row an
        /// unrelated weight bucket maps to.
        ///
        /// A weight of exactly 1 (or anything below it, NaN included, which reads as 1) is the LEGACY uniform
        /// draw, byte for byte: file order and one rng.Next(count), so an admin setting the dial to 1 gets the
        /// pre-weighting draw back and not merely a statistically equivalent one. Above the typo ceiling reads
        /// as the ceiling. Null for an empty shortlist.
        /// </summary>
        public static BossEntryDef PickBossRow(IReadOnlyList<BossEntryDef> rows, string familyId, double familyRowWeight, Random rng)
        {
            if (rows == null || rows.Count == 0)
                return null;

            var weight = double.IsNaN(familyRowWeight) || double.IsInfinity(familyRowWeight) || familyRowWeight <= 1.0
                ? 1.0
                : Math.Min(familyRowWeight, DungeonPopulationLimits.MaxBossFamilyRowWeight);

            if (weight == 1.0)
                return rows[rng.Next(rows.Count)];

            var ordered = rows.OrderBy(r => r.Wcid).ToList();

            double WeightOf(BossEntryDef row)
                => familyId != null && row.Families != null && row.Families.Contains(familyId) ? weight : 1.0;

            var total = ordered.Sum(WeightOf);
            var roll = rng.NextDouble() * total;

            foreach (var row in ordered)
            {
                roll -= WeightOf(row);

                if (roll < 0)
                    return row;
            }

            // Floating-point residue can leave roll a hair above zero after the last subtraction.
            return ordered[ordered.Count - 1];
        }

        /// <summary>A ratio dial as the plan carries it: NaN, Infinity or a negative value reads as 0 ("axis disabled").</summary>
        private static double NonNegativeOrZero(double value)
            => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0.0 : value;

        // ---- reach-up (owner ruling 2026-10-08) -------------------------------------------------------------

        /// <summary>
        /// The highest LIVE level among the role-0 (trash) members of the RAW species tables - 375 on the shipped
        /// roster (the #1032 bestiary). The raw dictionary, never the dungeon's fit projection: the pivot is a
        /// property of the authored roster, and a per-dungeon pivot would project the same gem two different
        /// ways depending on where it opened. Trash only, because the band's own statistics (the health median,
        /// the band standard) are taken over role-0 members, and the pivot is where those run out. 0 for an empty
        /// or missing roster, which <see cref="ReachUpPivot"/> reads as "no pivot".
        /// </summary>
        internal static int AuthoredTopLevel(IReadOnlyDictionary<string, SpeciesTableDef> species, Func<uint, int> levelOf)
        {
            if (species == null || levelOf == null)
                return 0;

            var top = 0;

            foreach (var table in species.Values)
            {
                if (table?.Members == null)
                    continue;

                foreach (var member in table.Members)
                {
                    if (member != null && member.Role == 0)
                        top = Math.Max(top, levelOf(member.Wcid));
                }
            }

            return top;
        }

        /// <summary>
        /// The pivot P = floor(<paramref name="authoredTop"/> / <paramref name="bandHigh"/>): the highest gem
        /// level whose natural band's HIGH edge still reaches no further than the authored roster does. 326 on
        /// the shipped roster (floor(375 / 1.15)). 0 ("no pivot", so no reach-up and no curve) for a non-positive
        /// top, or for a band high edge that is not a positive finite number (DungeonRosterBand sanitizes the
        /// live dial, so that only reaches here from a hand-built band).
        /// </summary>
        internal static int ReachUpPivot(int authoredTop, double bandHigh)
        {
            if (authoredTop <= 0 || double.IsNaN(bandHigh) || double.IsInfinity(bandHigh) || bandHigh <= 0)
                return 0;

            return (int)Math.Floor(authoredTop / bandHigh + 1e-9);
        }

        /// <summary>
        /// The reach-up scale k = level / pivot strictly above the pivot, and exactly 1.0 everywhere else
        /// (including "no pivot"). Exactly 1.0, not merely close to it, because the builder compares it with ==
        /// to decide whether to hand the caller's own levelOf delegate straight through.
        /// </summary>
        internal static double ReachUpScale(int level, int pivot)
            => pivot > 0 && level > pivot ? (double)level / pivot : 1.0;

        /// <summary>
        /// One creature's projected level, round(<paramref name="raw"/> x <paramref name="scale"/>), half away
        /// from zero. A level of 0 or below ("no data") stays as it is, so a missing weenie never projects into
        /// a band. Saturates into int.
        /// </summary>
        internal static int ProjectLevel(int raw, double scale)
        {
            if (raw <= 0 || scale == 1.0)
                return raw;

            return (int)Math.Clamp(Math.Round(raw * scale, MidpointRounding.AwayFromZero), 1, int.MaxValue);
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
        /// cut. THE LADDER DOES NEED TO COVER IT. The uplift target is always the RUN's level, which
        /// DungeonGemSpec.MaxRunLevel caps at 500 (2026-10-08 owner ruling; a 375 fragment plus a Mana Scarab
        /// presses to 410 today), a reach-up STAMP (DungeonSpawnPlanEntry.StampLevel, the same ruling) climbs
        /// to ceil(1.15 x run level), and the boss term reaches one above the highest stamp - 576 at a 500
        /// run. So Content/dungeons/dynamic/modifiers.json carries rungs through 375, one at 415, and the
        /// wiki curve's extrapolated rungs every 25 levels from 425 to 600
        /// (Source/.claude/skills/weenie-generator/references/wiki/monster_reward_curve.tsv, xp_source
        /// "extrap"). Beyond the top rung LadderXp returns that rung's flat value, so a ladder that stopped
        /// short would silently pay every level above it the same XP and the terms would go inert exactly
        /// where they matter most. Any future ceiling raise needs new rungs here in the same commit
        /// (ThreadDungeonStoreTests pins the top rung against the highest reachable boss level).
        ///
        /// A max, like the boss term, so it never lowers a creature whose own XpOverride is already worth
        /// more - which is the ordinary case for a high-XpOverride weenie that merely sits a few levels under
        /// the edge. Luminance needs no term of its own: DungeonPopulationBuilder.LuminanceFor derives from
        /// this same baseXp, so raising the anchor here raises both on one curve.
        /// </summary>
        /// <param name="bossLadderOnly">
        /// Boss normalization (dynamic_dungeons_boss_normalize), APPENDED after the existing optional parameter so
        /// no positional caller rebinds. When true and <paramref name="role"/> is the Boss with a stamped level,
        /// the boss is priced at LadderXp(<paramref name="bossLevel"/>) and nothing else: its authored XpOverride
        /// described a creature whose stats normalization has just replaced, so a level-500 any-family row
        /// headlining a 185 gem must not pay level-500 XP. Non-boss roles ignore it.
        /// </param>
        public static long BaseXp(IReadOnlyList<XpLadderRungDef> ladder, int? xpOverride, int creatureLevel, DungeonRole role, int bossLevel,
            int upliftLevel = 0, bool bossLadderOnly = false)
        {
            if (bossLadderOnly && role == DungeonRole.Boss && bossLevel > 0)
                return DungeonRewardMath.LadderXp(ladder, bossLevel);

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
        /// <remarks>
        /// Group Threads (ruling R16): on a group plan the kill is priced at H * B / T (trash and elite) or
        /// E * B / T (boss), T being GroupScaling.ShareTotal (see its summary for how it offsets the fellowship
        /// share). This is the ONE pricing site: the spawner stamps the result as XpOverride, and neither that
        /// stamp nor Fellowship.SplitXp is touched. A solo plan (GroupRosterSize 1) returns XpForKill's result
        /// untouched. A positive solo value
        /// never prices to 0, matching XpForKill's own "never worth nothing" floor; a disabled scale (0) stays 0.
        /// </remarks>
        public static long XpFor(DungeonSpawnPlan plan, long baseXp, DungeonRole role)
        {
            var xp = DungeonRewardMath.XpForKill(baseXp, role, plan.XpScale, plan.XpMultiplier, plan.BossXpMultiplier);

            if (plan.GroupRosterSize <= 1 || xp <= 0)
                return xp;

            var grouped = (long)Math.Clamp(Math.Round(xp * GroupRewardFactor(plan, role)), 0.0, DungeonRewardMath.MaxRepresentableLong);
            return Math.Max(1, grouped);
        }

        /// <summary>
        /// The plan's group reward factor for <paramref name="role"/>: the boss factor for the boss, the trash
        /// factor for trash and elites. A value that is not a positive finite number reads as 1.0 (no
        /// correction) rather than reaching an XP grant as NaN or 0. Only meaningful on a group plan.
        /// </summary>
        internal static double GroupRewardFactor(DungeonSpawnPlan plan, DungeonRole role)
        {
            var factor = role == DungeonRole.Boss ? plan.GroupBossRewardFactor : plan.GroupTrashRewardFactor;
            return double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0 ? 1.0 : factor;
        }

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

            // Group Threads (R16): the same per-kill correction as XpFor, before the one rounding. Solo skips it.
            if (plan.GroupRosterSize > 1)
                lum *= GroupRewardFactor(plan, role);

            return (int)Math.Clamp(Math.Round(lum, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        }
    }
}
