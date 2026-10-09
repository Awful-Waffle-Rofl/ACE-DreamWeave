using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// An inclusive integer level range, as used by the trash and elite bands (PLAN 1.5).
    /// </summary>
    public readonly struct LevelBand
    {
        public readonly int Low;
        public readonly int High;

        public LevelBand(int low, int high)
        {
            Low = low;
            High = high;
        }

        public bool Contains(int level) => level >= Low && level <= High;

        /// <summary>Integer midpoint, used as the fall-through target when the band holds no member.</summary>
        public int Centre => Low + (High - Low) / 2;

        public override string ToString() => $"{Low}-{High}";
    }

    /// <summary>
    /// The four trash/elite band multipliers (world_events_trash_band_low/high, world_events_elite_band_low/high),
    /// bundled so <see cref="WorldEventRosterSelector.DialSource"/> can hand them over in one call.
    /// </summary>
    public readonly struct BandDials
    {
        public readonly double TrashLow;
        public readonly double TrashHigh;
        public readonly double EliteLow;
        public readonly double EliteHigh;

        public BandDials(double trashLow, double trashHigh, double eliteLow, double eliteHigh)
        {
            TrashLow = trashLow;
            TrashHigh = trashHigh;
            EliteLow = eliteLow;
            EliteHigh = eliteHigh;
        }

        /// <summary>The built-in constants (<see cref="WorldEventRosterSelector.DefaultTrashBandLow"/> and friends).</summary>
        public static BandDials Defaults => new BandDials(
            WorldEventRosterSelector.DefaultTrashBandLow,
            WorldEventRosterSelector.DefaultTrashBandHigh,
            WorldEventRosterSelector.DefaultEliteBandLow,
            WorldEventRosterSelector.DefaultEliteBandHigh);
    }

    /// <summary>
    /// The level-only audience estimate (TECH-DESIGN 5.6, decision C3 - permanently simple; gear inputs
    /// are out of scope, and PowerSum is derived from levels alone). All four values are 0 for an empty
    /// audience.
    ///
    /// Declared with readonly fields rather than the mutable ones sketched in 5.6, because a
    /// "public readonly struct" cannot hold mutable instance fields.
    /// </summary>
    public readonly struct AudienceEstimate
    {
        public readonly int Count;
        public readonly int MedianLevel;
        public readonly int P90Level;

        /// <summary>
        /// The sorted-ascending participant levels the estimate was computed from (user directive
        /// 2026-08-16, proportional band selection). Never null - the constructors that take no list set this
        /// to an empty array, which is what makes <see cref="WorldEventRosterSelector.SampleParticipantLevel"/>
        /// fall back to <see cref="MedianLevel"/> and reproduces the pre-directive behaviour exactly.
        /// </summary>
        public readonly IReadOnlyList<int> Levels;

        /// <summary>
        /// The audience's POWER, as sum((clamp(level, 0, 275) / 275)^2) over the sampled players
        /// (BOSS-STANDARD.md section 3). Squared so a crowd of low-level bystanders barely moves it while a
        /// handful of endgame characters does - a level 275 contributes 1.0, a level 100 about 0.13.
        ///
        /// Only the named-boss health curve reads it. Every count-based dial (crowd health, wave size,
        /// kill target) still reads <see cref="Count"/>, and 0 is the correct value for an estimate built
        /// without a level list, which is why the constructor parameter is optional.
        /// </summary>
        public readonly double PowerSum;

        public AudienceEstimate(int count, int medianLevel, int p90Level, double powerSum = 0)
            : this(count, medianLevel, p90Level, Array.Empty<int>(), powerSum)
        {
        }

        /// <summary>From a level list: <see cref="Levels"/> is the list and <see cref="PowerSum"/> is computed from it.</summary>
        public AudienceEstimate(int count, int medianLevel, int p90Level, IReadOnlyList<int> levels)
            : this(count, medianLevel, p90Level, levels, WorldEventRosterSelector.PowerSum(levels))
        {
        }

        public AudienceEstimate(int count, int medianLevel, int p90Level, IReadOnlyList<int> levels, double powerSum)
        {
            Count = count;
            MedianLevel = medianLevel;
            P90Level = p90Level;
            Levels = levels ?? Array.Empty<int>();
            PowerSum = powerSum;
        }

        public override string ToString()
        {
            return $"count={Count} median={MedianLevel} p90={P90Level} power={PowerSum:F2}";
        }
    }

    /// <summary>
    /// What one wave pick produced (TECH-DESIGN 2.15). Two lists rather than one, because the two halves
    /// are budgeted differently: <see cref="Trash"/> is what fits under the theme's MaxAlive, and
    /// <see cref="Champions"/> is what the OVERFLOW bought, which is deliberately additive ABOVE MaxAlive.
    ///
    /// Both are still spawned through the wave path (kind Wave), so both count toward LiveCount and toward
    /// the cleared-field trigger, and neither ever touches BossGuid.
    ///
    /// WP-24: <see cref="EliteIndex"/>/<see cref="EliteSynthetic"/> mark the every-third-wave elite slot
    /// (index into <see cref="Trash"/>, -1 when this pick placed no elite) and <see cref="ChampionsSynthetic"/>
    /// parallels <see cref="Champions"/> one bool per entry. "Synthetic" means the slot could not be filled
    /// from a real in-band role-1/role-2 member and was instead promoted from a role-0 member in the same
    /// band (owner ruling 2026-08-16) - the spawner renames it and gives it extra health.
    /// </summary>
    public sealed class WavePick
    {
        public static readonly WavePick Empty = new WavePick(new List<uint>(), new List<uint>());

        public WavePick(IReadOnlyList<uint> trash, IReadOnlyList<uint> champions)
            : this(trash, champions, default, default)
        {
        }

        public WavePick(IReadOnlyList<uint> trash, IReadOnlyList<uint> champions, WaveSizing sizing)
            : this(trash, champions, sizing, default)
        {
        }

        public WavePick(IReadOnlyList<uint> trash, IReadOnlyList<uint> champions, WaveSizing sizing, LevelBand band)
            : this(trash, champions, sizing, band, -1, false, null)
        {
        }

        public WavePick(IReadOnlyList<uint> trash, IReadOnlyList<uint> champions, WaveSizing sizing, LevelBand band,
            int eliteIndex, bool eliteSynthetic, IReadOnlyList<bool> championsSynthetic)
            : this(trash, champions, sizing, band, eliteIndex, eliteSynthetic, championsSynthetic, null, null)
        {
        }

        public WavePick(IReadOnlyList<uint> trash, IReadOnlyList<uint> champions, WaveSizing sizing, LevelBand band,
            int eliteIndex, bool eliteSynthetic, IReadOnlyList<bool> championsSynthetic,
            IReadOnlyList<int> trashUplift, IReadOnlyList<int> championsUplift)
            : this(trash, champions, sizing, band, eliteIndex, eliteSynthetic, championsSynthetic, trashUplift, championsUplift, null, null)
        {
        }

        public WavePick(IReadOnlyList<uint> trash, IReadOnlyList<uint> champions, WaveSizing sizing, LevelBand band,
            int eliteIndex, bool eliteSynthetic, IReadOnlyList<bool> championsSynthetic,
            IReadOnlyList<int> trashUplift, IReadOnlyList<int> championsUplift,
            IReadOnlyList<int> trashSlotLevels, IReadOnlyList<int> championsSlotLevels)
        {
            TrashSlotLevels = trashSlotLevels ?? new List<int>();
            ChampionsSlotLevels = championsSlotLevels ?? new List<int>();
            TrashUplift = trashUplift ?? new List<int>();
            ChampionsUplift = championsUplift ?? new List<int>();
            Trash = trash ?? new List<uint>();
            Champions = champions ?? new List<uint>();
            Sizing = sizing;
            Band = band;
            EliteIndex = eliteIndex;
            EliteSynthetic = eliteSynthetic;
            ChampionsSynthetic = championsSynthetic ?? new List<bool>();
        }

        public IReadOnlyList<uint> Trash { get; }

        public IReadOnlyList<uint> Champions { get; }

        /// <summary>
        /// Band uplift level per <see cref="Trash"/> slot (world_events_band_uplift): the participant level L
        /// the slot was drawn against when its member sits BELOW the natural low edge floor(L * trash low),
        /// else 0 (no uplift). Parallel to <see cref="Trash"/>, but a list shorter than Trash (a pick built by
        /// a constructor that never carried one) reads as all zeros - see <see cref="TrashUpliftAt"/>.
        /// </summary>
        public IReadOnlyList<int> TrashUplift { get; }

        /// <summary>Band uplift level per <see cref="Champions"/> entry; same contract as <see cref="TrashUplift"/>.</summary>
        public IReadOnlyList<int> ChampionsUplift { get; }

        /// <summary>
        /// The participant level L each <see cref="Trash"/> slot was drawn against (clamped to
        /// <see cref="MaxConsideredLevel"/>), recorded for EVERY slot whether or not it is uplifted. The
        /// combat-trait guard measures its band standard against this level. A list shorter than Trash reads
        /// as 0 ("unknown") through <see cref="TrashSlotLevelAt"/>.
        /// </summary>
        public IReadOnlyList<int> TrashSlotLevels { get; }

        /// <summary>The slot level per <see cref="Champions"/> entry; same contract as <see cref="TrashSlotLevels"/>.</summary>
        public IReadOnlyList<int> ChampionsSlotLevels { get; }

        /// <summary>
        /// True when this pick recorded a slot level for every slot (a pick from <c>PickWave</c>); false for one
        /// built by a constructor that never carried them, whose slot levels all read 0 as "unknown".
        /// </summary>
        public bool HasSlotLevels => TrashSlotLevels.Count == Trash.Count && ChampionsSlotLevels.Count == Champions.Count;

        public int TrashSlotLevelAt(int index) => index >= 0 && index < TrashSlotLevels.Count ? TrashSlotLevels[index] : 0;

        public int ChampionSlotLevelAt(int index) => index >= 0 && index < ChampionsSlotLevels.Count ? ChampionsSlotLevels[index] : 0;

        public int TrashUpliftAt(int index) => index >= 0 && index < TrashUplift.Count ? TrashUplift[index] : 0;

        public int ChampionUpliftAt(int index) => index >= 0 && index < ChampionsUplift.Count ? ChampionsUplift[index] : 0;

        /// <summary>
        /// The arithmetic this pick came out of. The pace controller reads
        /// <see cref="WaveSizing.QuantityMaxed"/> off it when this wave later clears, to decide whether a
        /// speed-up goes to quantity or to health.
        /// </summary>
        public WaveSizing Sizing { get; }

        /// <summary>Index into <see cref="Trash"/> of the every-third-wave elite slot, or -1 when none.</summary>
        public int EliteIndex { get; }

        /// <summary>True when the elite slot is a role-0 promotion rather than a real role-1 elite.</summary>
        public bool EliteSynthetic { get; }

        /// <summary>One bool per <see cref="Champions"/> entry: true when that champion is a role-0 promotion.</summary>
        public IReadOnlyList<bool> ChampionsSynthetic { get; }

        /// <summary>
        /// The span of the per-slot trash bands actually drawn from this pick (user directive 2026-08-16,
        /// proportional band selection): Low is the lowest Low and High the highest High among the bands
        /// used across every trash slot. When no trash slot was drawn, this is <c>TrashBand(est.MedianLevel)</c>.
        /// Used only for the 5.2 wave log line's <c>band=lo-hi</c> field - it is a reporting span, not a
        /// band any single slot was drawn against.
        /// </summary>
        public LevelBand Band { get; }

        public int Total => Trash.Count + Champions.Count;

        public bool IsEmpty => Total == 0;

        public override string ToString() => $"trash={Trash.Count} champions={Champions.Count}";
    }

    /// <summary>
    /// What <see cref="WorldEventRosterSelector.PickChampion"/> produced (WP-24 follow-up, owner ruling
    /// 2026-08-16). <see cref="None"/> (Wcid 0) means the family has nothing at all in the elite/champion
    /// band and no role-0 member to promote either - the caller must decline the champion rather than fall
    /// back to an out-of-band member, exactly like the wave-path elite/champion slots.
    /// </summary>
    public readonly struct ChampionPick
    {
        public static readonly ChampionPick None = new ChampionPick(0, false);

        /// <summary>
        /// The band uplift level for the family champion: the p90 level it was drawn against when it sits
        /// below the natural trash low edge there, else 0. See <see cref="WavePick.TrashUplift"/>.
        /// </summary>
        public readonly int UpliftLevel;

        /// <summary>The p90 level the champion was drawn against (clamped to 275); the combat-trait guard's band level.</summary>
        public readonly int SlotLevel;

        public readonly uint Wcid;

        /// <summary>True when this is a role-0 member promoted into the champion slot rather than a real
        /// in-band role-2/role-1 member - the spawner renames it and gives it extra health.</summary>
        public readonly bool Synthetic;

        public ChampionPick(uint wcid, bool synthetic)
            : this(wcid, synthetic, 0)
        {
        }

        public ChampionPick(uint wcid, bool synthetic, int upliftLevel)
            : this(wcid, synthetic, upliftLevel, 0)
        {
        }

        public ChampionPick(uint wcid, bool synthetic, int upliftLevel, int slotLevel)
        {
            Wcid = wcid;
            Synthetic = synthetic;
            UpliftLevel = upliftLevel;
            SlotLevel = slotLevel;
        }

        public bool IsNone => Wcid == 0;

        public override string ToString() => IsNone ? "none" : $"wcid={Wcid} synthetic={Synthetic} uplift={UpliftLevel}";
    }

    /// <summary>
    /// The pure size arithmetic behind one wave pick (TECH-DESIGN 2.15, D6). Split out from
    /// <see cref="WorldEventRosterSelector.PickWave"/> so the whole cap/room/overflow rule can be tested
    /// without a family roster or an rng.
    /// </summary>
    public readonly struct WaveSizing
    {
        /// <summary>Uncapped demand: waveCount.base + ceil(perParticipant * count) + the pace bonus.</summary>
        public readonly int Raw;

        /// <summary>The most trash this pick could place: min(waveCount.cap, room under MaxAlive).</summary>
        public readonly int Ceiling;

        /// <summary>What is actually placed as trash: min(max(1, Raw), Ceiling).</summary>
        public readonly int TrashSize;

        /// <summary>Demand the cap and the room threw away: max(0, Raw - TrashSize).</summary>
        public readonly int Overflow;

        /// <summary>Overflow champions this pick may add, already capped by how many are alive.</summary>
        public readonly int ChampionCount;

        public WaveSizing(int raw, int ceiling, int trashSize, int overflow, int championCount)
        {
            Raw = raw;
            Ceiling = ceiling;
            TrashSize = trashSize;
            Overflow = overflow;
            ChampionCount = championCount;
        }

        /// <summary>
        /// True when quantity has nothing left to give at this pick - the trash size is already at its
        /// ceiling. The pace controller reads this to decide whether a speed-up goes to quantity or health.
        /// </summary>
        public bool QuantityMaxed => TrashSize >= Ceiling;

        public override string ToString() =>
            $"raw={Raw} ceiling={Ceiling} trash={TrashSize} overflow={Overflow} champions={ChampionCount}";
    }

    /// <summary>
    /// Pure roster and audience arithmetic (TECH-DESIGN 5.6, PLAN 1.5).
    ///
    /// Nothing here reads the world or the clock, which is what lets the whole selection rule set be unit
    /// tested (D6). The production entry points draw from ThreadSafeRandom; every one of them has an
    /// overload taking a System.Random so a test can pin the draw. The one exception is the trash/elite
    /// band multipliers: the single-arg <see cref="TrashBand(int)"/>/<see cref="EliteBand(int)"/> overloads
    /// read them from PropertyManager through the <see cref="DialSource"/> test seam, while the two-arg pure
    /// overloads that take explicit multipliers stay database-free.
    /// </summary>
    public static class WorldEventRosterSelector
    {
        /// <summary>
        /// WP-24 (owner ruling 2026-08-16): the highest level a wave/elite/champion pick will ever consider,
        /// for the audience estimate (median/p90), for every PER-SLOT sampled participant level (user
        /// directive 2026-08-16, proportional bands - a sampled level is clamped to this before it is handed
        /// to <see cref="TrashBand"/>/<see cref="EliteBand"/>, while
        /// <see cref="AudienceEstimate.Levels"/> itself stays raw), and for the trash/elite band high edges.
        /// The client's own level chart tops out here (see CLAUDE.md "Client patching is an absolute no"),
        /// and nothing on this fork raises a character above it today. RAISE THIS when post-retail leveling
        /// content lands.
        /// </summary>
        public const int MaxConsideredLevel = 275;

        /// <summary>
        /// WP-24: an elite band narrower than this many levels (owner ruling - a band collapsed to a single
        /// level, which happens once p90 itself is clamped at <see cref="MaxConsideredLevel"/>) is not worth
        /// treating as its own band; <see cref="EliteBand"/> falls back to <see cref="TrashBand"/> instead.
        /// </summary>
        public const int NarrowEliteBandFallbackWidth = 10;

        /// <summary>
        /// WorldEventRole 3, the NAMED BOSS role (BOSS-STANDARD.md section 1). A member carrying it is in
        /// the catalog so the family reads coherently and so "/worldevent list families" can report it, but
        /// it is never drawn by the wave or champion selection: a named boss arrives only when the boss
        /// axis explicitly asks for it by id. Every pool in this class therefore excludes it.
        /// </summary>
        public const int NamedBossRole = 3;

        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventRosterSelector));

        /// <summary>Built-in trash band LOW multiplier, used when world_events_trash_band_low is unreadable or invalid.</summary>
        public const double DefaultTrashBandLow = 1.0;

        /// <summary>Built-in trash band HIGH multiplier, used when world_events_trash_band_high is unreadable or invalid.</summary>
        public const double DefaultTrashBandHigh = 1.5;

        /// <summary>Built-in elite/champion band LOW multiplier, used when world_events_elite_band_low is unreadable or invalid.</summary>
        public const double DefaultEliteBandLow = 1.25;

        /// <summary>Built-in elite/champion band HIGH multiplier, used when world_events_elite_band_high is unreadable or invalid.</summary>
        public const double DefaultEliteBandHigh = 1.75;

        /// <summary>
        /// Test seam for the four band-dial server properties. Null-coalesces to <see cref="ReadDialsFromProperties"/>
        /// in practice (this field is never actually null - it is assigned that method group below - but a
        /// test may reassign it to a fixed <see cref="BandDials"/> and must restore it afterward). Only the
        /// single-arg <see cref="TrashBand(int)"/>/<see cref="EliteBand(int)"/> overloads read this; the pure
        /// overloads that take explicit multipliers never touch it.
        /// </summary>
        internal static Func<BandDials> DialSource = ReadDialsFromProperties;

        /// <summary>
        /// world_events_trash_band_low/high and world_events_elite_band_low/high, through PropertyManager. A
        /// property read that throws - a unit test has no shard config at all - reads as the built-in
        /// defaults, which is the only safe direction for a failure here.
        /// </summary>
        private static BandDials ReadDialsFromProperties()
        {
            try
            {
                return new BandDials(
                    PropertyManager.GetDouble("world_events_trash_band_low", DefaultTrashBandLow).Item,
                    PropertyManager.GetDouble("world_events_trash_band_high", DefaultTrashBandHigh).Item,
                    PropertyManager.GetDouble("world_events_elite_band_low", DefaultEliteBandLow).Item,
                    PropertyManager.GetDouble("world_events_elite_band_high", DefaultEliteBandHigh).Item);
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read the trash/elite band dial properties; falling back to built-in defaults", ex);

                return BandDials.Defaults;
            }
        }

        // ---- band uplift dials ----------------------------------------------------------------------------

        /// <summary>Built-in world_events_band_uplift default (ON, owner ruling: new settings default on).</summary>
        public const bool DefaultBandUpliftEnabled = true;

        /// <summary>Built-in world_events_min_slot_pool default: distinct trash wcids a slot's pool should hold.</summary>
        public const int DefaultMinSlotPool = 6;

        /// <summary>Built-in world_events_band_low_floor default: the lowest the trash band's LOW edge may step to, as a ratio of the slot level.</summary>
        public const double DefaultBandLowFloor = 0.60;

        /// <summary>
        /// The three band-uplift dials (world_events_band_uplift, world_events_min_slot_pool,
        /// world_events_band_low_floor), bundled so <see cref="UpliftDialSource"/> hands them over in one call.
        /// Deliberately WE-owned: this code never reads a dynamic_dungeons_* key.
        /// </summary>
        public readonly struct UpliftDials
        {
            public readonly bool Enabled;
            public readonly int MinSlotPool;
            public readonly double LowFloor;

            public UpliftDials(bool enabled, int minSlotPool, double lowFloor)
            {
                Enabled = enabled;
                MinSlotPool = minSlotPool;
                LowFloor = lowFloor;
            }

            public static UpliftDials Defaults => new UpliftDials(DefaultBandUpliftEnabled, DefaultMinSlotPool, DefaultBandLowFloor);

            public static UpliftDials Off => new UpliftDials(false, DefaultMinSlotPool, DefaultBandLowFloor);
        }

        /// <summary>
        /// Test seam for the band-uplift dials, the same contract as <see cref="DialSource"/>: a test may
        /// reassign it and must restore it afterward.
        /// </summary>
        internal static Func<UpliftDials> UpliftDialSource = ReadUpliftDialsFromProperties;

        /// <summary>The live dials, sanitized (see <see cref="SanitizeUplift"/>).</summary>
        internal static UpliftDials CurrentUpliftDials() => SanitizeUplift(UpliftDialSource());

        private static UpliftDials ReadUpliftDialsFromProperties()
        {
            try
            {
                return new UpliftDials(
                    PropertyManager.GetBool("world_events_band_uplift", DefaultBandUpliftEnabled).Item,
                    (int)Math.Clamp(PropertyManager.GetLong("world_events_min_slot_pool", DefaultMinSlotPool).Item, 0, 1000),
                    PropertyManager.GetDouble("world_events_band_low_floor", DefaultBandLowFloor).Item);
            }
            catch (Exception)
            {
                // A unit test (or a not-yet-initialized server) has no shard config: read as the built-in
                // defaults. Unlike the band dials this does not log - it is read per pick, and the band
                // dials' own read already reports a missing config once per call.
                return UpliftDials.Defaults;
            }
        }

        /// <summary>
        /// The band-uplift dials with garbage collapsed to something safe: a NaN/Infinity/non-positive floor
        /// reads as the built-in default and a floor above 1.0 reads as 1.0 (no widening), mirroring how the
        /// Threads equivalent treats its floor. A negative pool reads as 0 (widening off).
        /// </summary>
        internal static UpliftDials SanitizeUplift(UpliftDials dials)
        {
            var floor = dials.LowFloor;

            if (double.IsNaN(floor) || double.IsInfinity(floor) || floor <= 0)
                floor = DefaultBandLowFloor;
            else if (floor > 1.0)
                floor = 1.0;

            return new UpliftDials(dials.Enabled, Math.Max(0, dials.MinSlotPool), floor);
        }

        /// <summary>
        /// The trash band multipliers as <see cref="TrashBand(int, double, double)"/> will actually use them:
        /// a non-finite or non-positive value replaced by its built-in default. The band standard is measured
        /// with the same numbers the draw band used.
        /// </summary>
        public static (double Low, double High) EffectiveTrashMultipliers()
        {
            var dials = DialSource();

            return (IsValidMultiplier(dials.TrashLow) ? dials.TrashLow : DefaultTrashBandLow,
                    IsValidMultiplier(dials.TrashHigh) ? dials.TrashHigh : DefaultTrashBandHigh);
        }

        /// <summary>
        /// The uplift level for one slot (pure): <paramref name="slotLevel"/> when the member sits BELOW the
        /// natural low edge <paramref name="naturalLow"/>, else 0. A member at or above the low edge is in
        /// band (or above it) and is spawned exactly as authored.
        /// </summary>
        public static int UpliftLevelFor(int memberLevel, int slotLevel, int naturalLow)
        {
            return slotLevel > 0 && memberLevel < naturalLow ? slotLevel : 0;
        }

        /// <summary>
        /// The trash band for a slot level, WIDENED at its low edge until the pool holds
        /// <paramref name="minPool"/> distinct wcids or the ratio reaches <paramref name="lowFloor"/>
        /// (pure). The high edge never moves. Starts from the natural band, so a pool that is already deep
        /// enough returns it untouched; when the floor is never enough, returns the WIDEST band tried.
        ///
        /// Counts the same pool <see cref="SelectPool"/> draws from: role-0 members, or - for a family
        /// authored with no trash at all - every non-named-boss member.
        /// </summary>
        public static LevelBand WidenTrashBand(IReadOnlyList<FamilyMember> members, int level, double trashLow,
            double trashHigh, double lowFloor, int minPool)
        {
            var natural = TrashBand(level, trashLow, trashHigh);

            if (members == null || minPool <= 0)
                return natural;

            var basis = members.Where(m => m != null && m.Role == 0).ToList();

            if (basis.Count == 0)
                basis = members.Where(m => m != null && m.Role != NamedBossRole).ToList();

            if (basis.Count == 0)
                return natural;

            var startLow = IsValidMultiplier(trashLow) ? trashLow : DefaultTrashBandLow;
            var widest = natural;

            foreach (var ratio in DungeonRosterSelector.LowEdgeLadder(startLow, lowFloor))
            {
                widest = TrashBand(level, ratio, trashHigh);

                if (basis.Where(m => widest.Contains(m.Level)).Select(m => m.Wcid).Distinct().Count() >= minPool)
                    return widest;
            }

            return widest;
        }

        /// <summary>True for a multiplier that can meaningfully scale a level: finite and strictly positive.</summary>
        private static bool IsValidMultiplier(double multiplier) => !double.IsNaN(multiplier) && !double.IsInfinity(multiplier) && multiplier > 0;

        /// <summary>
        /// Trash band, using the world_events_trash_band_low/high server properties (built-in defaults 1.0/1.5).
        /// Reads <see cref="DialSource"/>; see <see cref="TrashBand(int, double, double)"/> for the pure form.
        /// </summary>
        public static LevelBand TrashBand(int medianLevel)
        {
            var dials = DialSource();

            return TrashBand(medianLevel, dials.TrashLow, dials.TrashHigh);
        }

        /// <summary>
        /// Trash band: [medianLevel * low, medianLevel * high], pure - no property reads.
        ///
        /// ROUNDING: the low edge floors and the high edge ceilings, so the integer band always CONTAINS
        /// the real-valued interval rather than clipping members that sit just inside it. At median 100 and
        /// the built-in defaults (1.0/1.5) that is [100, 150] - level 100 and level 150 are both in, 99 and
        /// 151 are both out.
        ///
        /// WP-24: the high edge additionally clamps to <see cref="MaxConsideredLevel"/>, so a level-275
        /// audience is never handed a level-300+ trash member. A non-finite or non-positive <paramref
        /// name="low"/>/<paramref name="high"/> falls back to <see cref="DefaultTrashBandLow"/>/<see
        /// cref="DefaultTrashBandHigh"/>. If the rounded high edge ends up below the low edge (a
        /// pathologically low high multiplier), the high edge is raised to match the low edge rather than
        /// returning an inverted band.
        /// </summary>
        public static LevelBand TrashBand(int medianLevel, double low, double high)
        {
            if (!IsValidMultiplier(low))
                low = DefaultTrashBandLow;

            if (!IsValidMultiplier(high))
                high = DefaultTrashBandHigh;

            var level = Math.Max(0, medianLevel);

            var lowEdge = (int)Math.Floor(level * low);
            var highEdge = Math.Min(MaxConsideredLevel, (int)Math.Ceiling(level * high));

            if (highEdge < lowEdge)
                highEdge = lowEdge;

            return new LevelBand(lowEdge, highEdge);
        }

        /// <summary>
        /// Elite / champion band, using the world_events_elite_band_low/high server properties (built-in
        /// defaults 1.25/1.75). Reads <see cref="DialSource"/>; see <see cref="EliteBand(int, double, double)"/>
        /// for the pure form.
        /// </summary>
        public static LevelBand EliteBand(int p90Level)
        {
            var dials = DialSource();

            return EliteBand(p90Level, dials.EliteLow, dials.EliteHigh, dials.TrashLow, dials.TrashHigh);
        }

        /// <summary>
        /// Elite / champion band: [p90Level * low, p90Level * high], pure - no property reads. Same edge
        /// rounding as <see cref="TrashBand(int, double, double)"/>. At p90 150 and the built-in defaults
        /// (1.25/1.75) that is [187, 263] (150 * 1.25 = 187.5 -> floor 187; 150 * 1.75 = 262.5 -> ceil 263).
        ///
        /// WP-24: the high edge clamps to <see cref="MaxConsideredLevel"/> (a level-275 audience is never
        /// handed a level-300+ elite), and once that clamp (or a tight <paramref name="low"/>/<paramref
        /// name="high"/> pair) narrows the band below <see cref="NarrowEliteBandFallbackWidth"/> levels wide,
        /// this returns <see cref="TrashBand(int, double, double)"/> at the BUILT-IN trash DEFAULT constants
        /// (this 3-arg overload has no configured trash multipliers to fall back to - see
        /// <see cref="EliteBand(int, double, double, double, double)"/> for the 5-arg form that takes them)
        /// instead, so the elite/champion draw still has a real range to work with rather than a single
        /// collapsed level.
        /// </summary>
        public static LevelBand EliteBand(int p90Level, double low, double high)
        {
            return EliteBand(p90Level, low, high, DefaultTrashBandLow, DefaultTrashBandHigh);
        }

        /// <summary>
        /// Elite / champion band, pure - no property reads, with the narrow-band trash fallback taking its
        /// OWN explicit multipliers (<paramref name="trashLow"/>/<paramref name="trashHigh"/>) rather than
        /// the built-in trash defaults. This is what <see cref="EliteBand(int)"/> calls with the currently
        /// configured trash dials, so a narrow-elite fallback under a live run reflects whatever
        /// world_events_trash_band_low/high are actually set to, not the compiled-in constants; see
        /// <see cref="EliteBand(int, double, double)"/> for the 3-arg form that always uses the constants.
        /// </summary>
        public static LevelBand EliteBand(int p90Level, double low, double high, double trashLow, double trashHigh)
        {
            if (!IsValidMultiplier(low))
                low = DefaultEliteBandLow;

            if (!IsValidMultiplier(high))
                high = DefaultEliteBandHigh;

            var level = Math.Max(0, p90Level);

            var lowEdge = (int)Math.Floor(level * low);
            var highEdge = Math.Min(MaxConsideredLevel, (int)Math.Ceiling(level * high));

            if (highEdge < lowEdge)
                highEdge = lowEdge;

            if (highEdge - lowEdge < NarrowEliteBandFallbackWidth)
                return TrashBand(level, trashLow, trashHigh);

            return new LevelBand(lowEdge, highEdge);
        }

        /// <summary>
        /// The level a character has to reach to contribute a full 1.0 to
        /// <see cref="AudienceEstimate.PowerSum"/> (BOSS-STANDARD.md section 3). Levels above it are
        /// clamped rather than allowed to contribute more than one whole player's worth.
        /// </summary>
        public const int PowerReferenceLevel = 275;

        /// <summary>
        /// Median is the lower-middle element for an even count; p90 is the element at
        /// ceil(0.9 * n) - 1, clamped into range. Both are 0 for an empty list.
        ///
        /// WP-24: both are additionally clamped to <see cref="MaxConsideredLevel"/> before being returned,
        /// so a run staged near a handful of high-level characters never computes a band above what the
        /// fork actually considers a real level. <see cref="AudienceEstimate.Levels"/> keeps the RAW sorted
        /// levels - the clamp is re-applied per slot where a sampled level meets a band.
        ///
        /// PowerSum is sum((clamp(level, 0, PowerReferenceLevel) / PowerReferenceLevel)^2), and is also 0
        /// for an empty list.
        /// </summary>
        public static AudienceEstimate EstimateFromLevels(IReadOnlyList<int> levels)
        {
            if (levels == null || levels.Count == 0)
                return new AudienceEstimate(0, 0, 0);

            var sorted = new List<int>(levels);
            sorted.Sort();

            var n = sorted.Count;

            var median = Math.Min(MaxConsideredLevel, sorted[(n - 1) / 2]);

            var p90Index = (int)Math.Ceiling(0.9 * n) - 1;

            if (p90Index < 0)
                p90Index = 0;
            else if (p90Index > n - 1)
                p90Index = n - 1;

            var p90 = Math.Min(MaxConsideredLevel, sorted[p90Index]);

            return new AudienceEstimate(n, median, p90, sorted);
        }

        /// <summary>sum((clamp(level, 0, PowerReferenceLevel) / PowerReferenceLevel)^2) (D6 - pure).</summary>
        public static double PowerSum(IReadOnlyList<int> levels)
        {
            if (levels == null || levels.Count == 0)
                return 0;

            var total = 0.0;

            foreach (var level in levels)
            {
                var clamped = level < 0 ? 0 : (level > PowerReferenceLevel ? PowerReferenceLevel : level);

                var share = clamped / (double)PowerReferenceLevel;

                total += share * share;
            }

            return total;
        }

        /// <summary>
        /// The participant count the scaling floor stands in for. An estimate at or above this is passed
        /// through untouched.
        /// </summary>
        public const int FloorParticipantCount = 1;

        /// <summary>The level the single floor participant is treated as being.</summary>
        public const int FloorParticipantLevel = 50;

        /// <summary>
        /// Exactly one participant at <see cref="FloorParticipantLevel"/>: count 1, median and p90 50, and a
        /// one-element level list so <see cref="AudienceEstimate.PowerSum"/> is COMPUTED - (50/275)^2 - rather
        /// than left at the 0 the list-free constructor would give it. A zero power sum would leave the named
        /// boss curve at exactly 1.0, which is the degenerate case this floor exists to remove.
        ///
        /// The list is wrapped read-only because this is a shared static: nothing that receives the estimate
        /// may edit the levels behind it.
        /// </summary>
        public static readonly AudienceEstimate FloorEstimate =
            new AudienceEstimate(FloorParticipantCount, FloorParticipantLevel, FloorParticipantLevel,
                Array.AsReadOnly(new[] { FloorParticipantLevel }));

        /// <summary>
        /// The scaling floor (D6 - pure, no world access): an estimate that saw at least
        /// <see cref="FloorParticipantCount"/> participant is returned unchanged, and anything below it reads
        /// as <see cref="FloorEstimate"/> instead.
        ///
        /// This exists so a run that samples nobody - a staff-only field, a sampler that threw, a landblock
        /// nobody had reached yet - still sizes against a real, if minimal, audience rather than against
        /// zero, which collapses every band to level 0. It is applied at <c>WorldEvent.SetAudience</c> and
        /// NOWHERE else; in particular <see cref="EstimateFromLevels"/> keeps returning a genuine zero for an
        /// empty list, because it is also the "/worldevent simulate" entry point for an explicitly given
        /// level list.
        /// </summary>
        public static AudienceEstimate WithFloor(AudienceEstimate est)
        {
            return est.Count >= FloorParticipantCount ? est : FloorEstimate;
        }

        /// <summary>
        /// The wcids for one wave (PLAN 1.5, TECH-DESIGN 5.6).
        ///
        /// Size = theme.WaveCount.Resolve(audience count), capped at what is left of theme.MaxAlive and
        /// floored at 1 while there is any room at all - so a wave is never empty on a live field and is
        /// always empty on a full one. Composition is role-0 trash, one participant level sampled PER SLOT
        /// (user directive 2026-08-16: proportional to who is present, not anchored on the audience
        /// median - see <see cref="SampleParticipantLevel"/>) and a member drawn uniformly at random from
        /// that level's trash band with repetition; every third wave (index 2, 5, 8, ...) upgrades one slot
        /// to a role-1 elite from the elite band of one sampled participant level, when the family has one.
        /// </summary>
        public static WavePick PickWave(FamilyDef family, AudienceEstimate est, SourceThemeDef theme,
            int waveIndex, int currentAlive)
        {
            return PickWave(family, est, theme, waveIndex, currentAlive, 0, 0, null);
        }

        public static WavePick PickWave(FamilyDef family, AudienceEstimate est, SourceThemeDef theme,
            int waveIndex, int currentAlive, Random rng)
        {
            return PickWave(family, est, theme, waveIndex, currentAlive, 0, 0, rng);
        }

        /// <summary>
        /// The full pick (TECH-DESIGN 2.15). <paramref name="quantityBonus"/> is the pace controller's
        /// dial, added to the UNCAPPED size so it feeds both the trash count and the overflow;
        /// <paramref name="championsAlive"/> is how many overflow champions this run already has standing,
        /// which is the only thing bounding them.
        ///
        /// Trash and the elite/champion slots each sample ONE participant level (user directive
        /// 2026-08-16) rather than anchoring on the audience median/p90, so the produced monster levels are
        /// proportional to who is actually present: 9 level-50s and 1 level-275 yield roughly 90% of the
        /// wave from the level-50 band and 10% from a level-275 band. <see cref="PickChampion"/> (the family
        /// boss) is unaffected and stays p90-anchored.
        /// </summary>
        public static WavePick PickWave(FamilyDef family, AudienceEstimate est, SourceThemeDef theme,
            int waveIndex, int currentAlive, int quantityBonus, int championsAlive, Random rng)
        {
            if (theme == null || family?.Members == null || family.Members.Count == 0)
                return WavePick.Empty;

            var sizing = SizeWave(theme, est.Count, currentAlive, quantityBonus, championsAlive);

            if (sizing.TrashSize <= 0 && sizing.ChampionCount <= 0)
                return new WavePick(null, null, sizing, TrashBand(est.MedianLevel));

            rng = rng ?? NewRandom();

            var result = new List<uint>();

            var eliteIndex = -1;
            var eliteSynthetic = false;

            var lowBand = int.MaxValue;
            var highBand = int.MinValue;

            // Band uplift (world_events_band_uplift). Read ONCE per pick. Off reads as "never widen, never
            // uplift", which is the pre-uplift behaviour exactly - the rng is drawn the same way either way.
            var uplift = SanitizeUplift(UpliftDialSource());
            var (trashLow, trashHigh) = EffectiveTrashMultipliers();

            var trashUplift = new List<int>();
            var championsUplift = new List<int>();
            var trashSlotLevels = new List<int>();
            var championsSlotLevels = new List<int>();

            if (sizing.TrashSize > 0)
            {
                var poolsByLevel = new Dictionary<int, (List<FamilyMember> Pool, int NaturalLow)>();

                for (var i = 0; i < sizing.TrashSize; i++)
                {
                    var level = ConsideredLevel(SampleParticipantLevel(est, rng));

                    if (!poolsByLevel.TryGetValue(level, out var slotPool))
                    {
                        var slotBand = TrashBand(level);

                        if (slotBand.Low < lowBand)
                            lowBand = slotBand.Low;
                        if (slotBand.High > highBand)
                            highBand = slotBand.High;

                        // A thin pool widens down to the floor BEFORE SelectPool's nearest-member fallback, so a
                        // family with one in-band member no longer fills every slot with copies of it.
                        var drawBand = uplift.Enabled
                            ? WidenTrashBand(family.Members, level, trashLow, trashHigh, uplift.LowFloor, uplift.MinSlotPool)
                            : slotBand;

                        slotPool = (SelectPool(family.Members, drawBand, role: 0, strictRole: false), slotBand.Low);
                        poolsByLevel[level] = slotPool;
                    }

                    var trash = slotPool.Pool;

                    if (trash.Count > 0)
                    {
                        var member = trash[rng.Next(trash.Count)];

                        result.Add(member.Wcid);
                        trashUplift.Add(uplift.Enabled ? UpliftLevelFor(member.Level, level, slotPool.NaturalLow) : 0);
                        trashSlotLevels.Add(level);
                    }
                }

                // WP-24 (owner ruling 2026-08-16): a real role-1 elite from the band, or nothing - the old
                // nearest-band fall-through is gone, because it is what handed a 12-level-50 audience a
                // level-300 Captain Keeson. When the band has no real elite, a role-0 member drawn from the
                // SAME band is promoted instead (synthetic = true); the spawner renames and health-buffs it.
                //
                // The band comes from ONE sampled participant level (user directive 2026-08-16), not from
                // the audience p90, so the elite is level-equivalent with somebody actually present.
                if (result.Count > 0 && waveIndex % 3 == 2)
                {
                    var eliteLevel = ConsideredLevel(SampleParticipantLevel(est, rng));
                    var eliteBand = EliteBand(eliteLevel);

                    var elites = ElitePool(family.Members, eliteBand);

                    FamilyMember chosen = null;

                    if (elites.Count > 0)
                    {
                        chosen = elites[rng.Next(elites.Count)];
                    }
                    else
                    {
                        var promotion = PromotionPool(family.Members, eliteBand);

                        if (promotion.Count > 0)
                        {
                            chosen = promotion[rng.Next(promotion.Count)];
                            eliteSynthetic = true;
                        }
                    }

                    if (chosen != null)
                    {
                        eliteIndex = rng.Next(result.Count);
                        result[eliteIndex] = chosen.Wcid;
                        trashUplift[eliteIndex] = uplift.Enabled ? UpliftLevelFor(chosen.Level, eliteLevel, TrashBand(eliteLevel).Low) : 0;
                        trashSlotLevels[eliteIndex] = eliteLevel;
                    }
                }
            }

            var champions = new List<uint>();
            var championsSynthetic = new List<bool>();

            if (sizing.ChampionCount > 0)
            {
                // Same rule as the elite slot: ChampionPool first (role-2 in band, else role-1 in band),
                // then a role-0 promotion from the same band, then nothing for that slot. Each champion
                // samples its OWN participant level (user directive 2026-08-16), so the pools are rebuilt
                // per slot rather than once for the whole overflow.
                for (var i = 0; i < sizing.ChampionCount; i++)
                {
                    var championLevel = ConsideredLevel(SampleParticipantLevel(est, rng));
                    var championBand = EliteBand(championLevel);

                    var pool = ChampionPool(family.Members, championBand);

                    if (pool.Count > 0)
                    {
                        var real = pool[rng.Next(pool.Count)];

                        champions.Add(real.Wcid);
                        championsSynthetic.Add(false);
                        championsUplift.Add(uplift.Enabled ? UpliftLevelFor(real.Level, championLevel, TrashBand(championLevel).Low) : 0);
                        championsSlotLevels.Add(championLevel);
                        continue;
                    }

                    var promotionPool = PromotionPool(family.Members, championBand);

                    if (promotionPool.Count > 0)
                    {
                        var promoted = promotionPool[rng.Next(promotionPool.Count)];

                        champions.Add(promoted.Wcid);
                        championsSynthetic.Add(true);
                        championsUplift.Add(uplift.Enabled ? UpliftLevelFor(promoted.Level, championLevel, TrashBand(championLevel).Low) : 0);
                        championsSlotLevels.Add(championLevel);
                    }
                }
            }

            var band = lowBand <= highBand ? new LevelBand(lowBand, highBand) : TrashBand(est.MedianLevel);

            return new WavePick(result, champions, sizing, band, eliteIndex, eliteSynthetic, championsSynthetic, trashUplift, championsUplift,
                trashSlotLevels, championsSlotLevels);
        }

        /// <summary>
        /// One participant drawn uniformly at random, so a slot anchored on it is proportional to who is
        /// present: 9 level 50s and 1 level 275 anchor ~90% of slots at 50 and ~10% at 275 (user directive
        /// 2026-08-16). Empty Levels (a 3-arg estimate, or nobody in range) falls back to the median, which
        /// preserves the pre-directive behaviour exactly.
        /// </summary>
        public static int SampleParticipantLevel(AudienceEstimate est, Random rng)
        {
            if (est.Levels != null && est.Levels.Count > 0)
                return est.Levels[rng.Next(est.Levels.Count)];

            return est.MedianLevel;
        }

        /// <summary>
        /// A participant level as the band arithmetic is allowed to see it: clamped to
        /// <see cref="MaxConsideredLevel"/> (WP-24, owner ruling 2026-08-16). Applied where a sampled level
        /// meets <see cref="TrashBand"/>/<see cref="EliteBand"/> rather than inside
        /// <see cref="SampleParticipantLevel"/>, so <see cref="AudienceEstimate.Levels"/> and the sample
        /// itself stay the raw player levels.
        /// </summary>
        private static int ConsideredLevel(int level)
        {
            return level > MaxConsideredLevel ? MaxConsideredLevel : level;
        }

        /// <summary>
        /// The size half of a pick (TECH-DESIGN 2.15, D6 - pure, no roster and no rng).
        ///
        /// The trash arithmetic is exactly what it was before overflow champions existed - min of the
        /// resolved wave count, the theme cap and the room left under MaxAlive, floored at 1 while there is
        /// any room at all - with the pace bonus folded into the UNCAPPED demand before the clamp. What is
        /// new is that the clamped-away remainder is no longer discarded: every
        /// theme.overflowPerChampion of it buys one role-2 champion, up to
        /// theme.overflowChampionMaxAlive standing at once.
        ///
        /// The champion count deliberately does NOT consult the room: overflow champions are additive above
        /// MaxAlive by design, so a full field still gets them (and a full field is precisely the state that
        /// produces overflow in the first place).
        /// </summary>
        public static WaveSizing SizeWave(SourceThemeDef theme, int participantCount, int currentAlive,
            int quantityBonus, int championsAlive)
        {
            if (theme == null)
                return new WaveSizing(0, 0, 0, 0, 0);

            var room = Math.Max(0, Math.Max(0, theme.EffectiveMaxAlive) - Math.Max(0, currentAlive));

            var cap = theme.WaveCount != null ? theme.EffectiveWaveCountCap : 1;

            var raw = (theme.WaveCount != null ? theme.ResolveWaveCountRaw(Math.Max(0, participantCount)) : 1) + Math.Max(0, quantityBonus);

            var ceiling = Math.Max(0, Math.Min(cap, room));

            // Floored at 1 rather than at the theme base: the backstop's job is only to guarantee that a
            // wave with room to land is never empty.
            var trashSize = Math.Min(Math.Max(1, raw), ceiling);

            var overflow = Math.Max(0, raw - trashSize);

            var championCount = 0;

            var perChampion = theme.EffectiveOverflowPerChampion;

            if (perChampion > 0)
            {
                var room4Champions = Math.Max(0, theme.OverflowChampionMaxAlive - Math.Max(0, championsAlive));

                championCount = Math.Max(0, Math.Min(room4Champions, overflow / perChampion));
            }

            return new WaveSizing(raw, ceiling, trashSize, overflow, championCount);
        }

        /// <summary>
        /// The pool an OVERFLOW champion is drawn from (TECH-DESIGN 2.15, WP-24): role-2 members IN the
        /// elite band, else role-1 members IN the elite band, else EMPTY.
        ///
        /// WP-24 (owner ruling 2026-08-16): the old nearest-band fall-through is gone - a champion slot with
        /// nothing in band is no longer filled by an out-of-band role-1/role-2 member (that is what handed a
        /// 12-level-50 audience a level-300 Captain Keeson). An empty pool here means the caller falls back
        /// to <see cref="PromotionPool"/> instead.
        /// </summary>
        private static List<FamilyMember> ChampionPool(IReadOnlyList<FamilyMember> members, LevelBand band)
        {
            var pool = InBandStrict(members, band, role: 2);

            if (pool.Count > 0)
                return pool;

            return InBandStrict(members, band, role: 1);
        }

        /// <summary>
        /// The elite-slot pool (TECH-DESIGN 2.15, WP-24): role-1 members IN the elite band, else EMPTY - see
        /// <see cref="ChampionPool"/>'s remarks for why the old nearest-band fall-through was removed.
        /// </summary>
        private static List<FamilyMember> ElitePool(IReadOnlyList<FamilyMember> members, LevelBand band)
        {
            return InBandStrict(members, band, role: 1);
        }

        /// <summary>
        /// WP-24 (owner ruling 2026-08-16): the role-0 promotion pool an elite/champion slot falls back to
        /// once <see cref="ElitePool"/>/<see cref="ChampionPool"/> come back empty - a member from the SAME
        /// band the elite/champion draw was looking in, promoted with a "Champion "/"Elite " name prefix and
        /// extra health rather than left as plain trash.
        ///
        /// Role-0 members IN the band, else the role-0 member(s) NEAREST the band centre by level - the same
        /// nearest-band fall-through the ordinary trash draw uses (<see cref="SelectPool"/>), but restricted
        /// to role 0 ONLY: unlike the trash draw, this must NEVER escape to a member of another role when the
        /// family has no role-0 member at all. That escape hatch exists on <see cref="SelectPool"/> purely so
        /// a wave is never empty for a family authored with no trash; reusing it here would let an
        /// elite/champion slot promote an OUT-OF-BAND role-1/role-2 member through the back door, which is
        /// exactly the fallback this whole change removes. A family with zero role-0 members returns EMPTY,
        /// and the caller (<see cref="PickWave"/>, <see cref="PickChampion"/>) declines the slot rather than
        /// reaching for anything else.
        /// </summary>
        private static List<FamilyMember> PromotionPool(IReadOnlyList<FamilyMember> members, LevelBand band)
        {
            var pool = members.Where(m => m != null && m.Role == 0).ToList();

            if (pool.Count == 0)
                return pool;

            var inBand = pool.Where(m => band.Contains(m.Level)).ToList();

            if (inBand.Count > 0)
                return inBand;

            var centre = band.Centre;
            var nearest = pool.Min(m => Math.Abs(m.Level - centre));

            return pool.Where(m => Math.Abs(m.Level - centre) == nearest).ToList();
        }

        /// <summary>
        /// Members of the given role that sit inside the band, and ONLY those - no nearest-band fall-through
        /// (WP-24). Used by <see cref="ElitePool"/> and <see cref="ChampionPool"/>, which must come back
        /// empty rather than reach for an out-of-band member.
        /// </summary>
        private static List<FamilyMember> InBandStrict(IReadOnlyList<FamilyMember> members, LevelBand band, int role)
        {
            return members.Where(m => m != null && m.Role == role && band.Contains(m.Level)).ToList();
        }

        /// <summary>
        /// The family champion - the once-per-run boss the kill_boss goal and the escalation path both place
        /// (PLAN 1.5, TECH-DESIGN 5.6), in preference order:
        ///   1. the highest-Level role-2 member IN the elite band;
        ///   2. the highest-Level role-1 member IN the elite band;
        ///   3. the highest-Level role-0 member from the SAME band, promoted (synthetic).
        /// Returns <see cref="ChampionPick.None"/> only when none of those three has anything - i.e. the
        /// family has nothing at all in band and no role-0 member to promote either.
        ///
        /// WP-24 follow-up (owner ruling 2026-08-16): this used to fall back to the highest role-2 anywhere
        /// and then to the highest member of ANY role, which is what let a level-50 audience's lugian family
        /// still produce a level-300 Captain Keeson at the champion mark. That out-of-band fallback is gone -
        /// this is now exactly <see cref="ChampionPool"/> then <see cref="PromotionPool"/>, the same shape
        /// the wave-path elite/champion slots use. Level ties break on the lower wcid within whichever pool
        /// answered, so the pick is stable for a given roster and band.
        ///
        /// <see cref="NamedBossRole"/> members are filtered out before any of that: a named boss arrives
        /// only when the boss axis asks for it by id, never through the family-champion slot.
        /// </summary>
        public static ChampionPick PickChampion(FamilyDef family, AudienceEstimate est)
        {
            if (family?.Members == null || family.Members.Count == 0)
                return ChampionPick.None;

            var members = family.Members.Where(m => m != null && m.Role != NamedBossRole).ToList();

            if (members.Count == 0)
                return ChampionPick.None;

            var band = EliteBand(est.P90Level);

            var pool = ChampionPool(members, band);

            if (pool.Count > 0)
                return WithUplift(HighestMember(pool), false, est.P90Level);

            var promotion = PromotionPool(members, band);

            if (promotion.Count > 0)
                return WithUplift(HighestMember(promotion), true, est.P90Level);

            return ChampionPick.None;
        }

        /// <summary>
        /// Members of the given role that sit inside the band. When the band holds none, falls through to
        /// the members of that role NEAREST the band centre by |Level - centre| (PLAN 1.5 "falls through to
        /// its nearest band"), keeping every equally-near member so the draw still has variety.
        ///
        /// Additionally falls back to members of ANY role when the role itself is unrepresented, which is
        /// what keeps a wave from coming back empty for a family whose members were all authored as elites.
        /// That any-role escape is deliberately NOT reused by <see cref="PromotionPool"/> (which has its own,
        /// role-0-only copy of the in-band-or-nearest rule below) - an elite/champion promotion must never
        /// reach an out-of-band role-1/role-2 member through it.
        ///
        /// WP-24: this method (any-role escape included) is now used only for the role-0 trash draw. The
        /// strict role-1/role-2 elite and champion pools (<see cref="ElitePool"/>, <see cref="ChampionPool"/>)
        /// go through <see cref="InBandStrict"/> instead, which declines rather than reaching for an
        /// out-of-band member - the old strict mode this
        /// method used to offer for them (see git history for WP-24) is what handed a 12-level-50 audience a
        /// level-300 Captain Keeson. <paramref name="strictRole"/> true (which now no caller passes) simply
        /// declines instead of taking the any-role escape.
        ///
        /// The any-role fallback excludes <see cref="NamedBossRole"/>. Without that exclusion a family
        /// whose only authored members are its named boss would put a 10 m boss into a trash wave, because
        /// the fallback's whole purpose is to never come back empty.
        /// </summary>
        private static List<FamilyMember> SelectPool(IReadOnlyList<FamilyMember> members, LevelBand band,
            int role, bool strictRole)
        {
            var pool = members.Where(m => m != null && m.Role == role).ToList();

            if (pool.Count == 0)
            {
                if (strictRole)
                    return pool;

                pool = members.Where(m => m != null && m.Role != NamedBossRole).ToList();
            }

            if (pool.Count == 0)
                return pool;

            var inBand = pool.Where(m => band.Contains(m.Level)).ToList();

            if (inBand.Count > 0)
                return inBand;

            var centre = band.Centre;
            var nearest = pool.Min(m => Math.Abs(m.Level - centre));

            return pool.Where(m => Math.Abs(m.Level - centre) == nearest).ToList();
        }

        /// <summary>
        /// The champion pick for <paramref name="member"/>, carrying its band uplift level against the p90
        /// slot level (<see cref="WavePick.TrashUplift"/> explains the rule) when the dial is on.
        /// </summary>
        private static ChampionPick WithUplift(FamilyMember member, bool synthetic, int p90Level)
        {
            var uplift = SanitizeUplift(UpliftDialSource());
            var level = ConsideredLevel(p90Level);

            var upliftLevel = uplift.Enabled ? UpliftLevelFor(member.Level, level, TrashBand(level).Low) : 0;

            return new ChampionPick(member.Wcid, synthetic, upliftLevel, level);
        }

        private static FamilyMember HighestMember(List<FamilyMember> members)
        {
            var best = members[0];

            foreach (var member in members)
            {
                if (member.Level > best.Level || (member.Level == best.Level && member.Wcid < best.Wcid))
                    best = member;
            }

            return best;
        }

        /// <summary>
        /// int.MaxValue is excluded because ThreadSafeRandom.Next(int, int) is INCLUSIVE of its upper bound
        /// and computes max + 1 internally, which overflows at int.MaxValue.
        /// </summary>
        private static Random NewRandom()
        {
            return new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));
        }
    }
}
