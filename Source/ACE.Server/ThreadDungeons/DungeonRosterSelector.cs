using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>Role a run-owned creature was placed as. Drives XP rate and modifier application.</summary>
    public enum DungeonRole
    {
        Trash = 0,
        Elite = 1,
        Boss = 2,
    }

    public sealed class DungeonRosterPick
    {
        public uint Wcid { get; }
        public DungeonRole Role { get; }

        public DungeonRosterPick(uint wcid, DungeonRole role)
        {
            Wcid = wcid;
            Role = role;
        }
    }

    /// <summary>
    /// The roster band's two edge multipliers (dynamic_dungeons_roster_band_low/high), bundled so every
    /// public DungeonRosterSelector method can take one optional parameter instead of two.
    ///
    /// SANITIZE RULE, and it is deliberately NOT the same rule ThreadDungeonSpawner.SanitizeDoubleDial uses
    /// for the run's other double dials: there, 0 is a valid explicit "disable this axis". Here it is not. A
    /// band edge of 0 is not a disabled axis, it is a DEGENERATE one - a zero low edge admits every creature
    /// in the game regardless of the gem's level, and a zero high edge (when low is left positive) collapses
    /// the band onto the low edge and admits almost nothing. Neither is a meaningful tuning choice, so NaN,
    /// Infinity AND any value <= 0 all fall back to the corresponding compiled default
    /// (<see cref="DungeonRosterSelector.BandLow"/>/<see cref="DungeonRosterSelector.BandHigh"/>), and each
    /// edge is separately clamped at its own typo ceiling
    /// (<see cref="DungeonRosterSelector.MaxBandLow"/>/<see cref="DungeonRosterSelector.MaxBandHigh"/>).
    ///
    /// The inverted-band guard (a high edge landing below the low edge after rounding) stays where it already
    /// lived, in DungeonRosterSelector.Band - this struct only sanitizes the two inputs, it does not
    /// re-implement the guard that makes their combination total.
    /// </summary>
    public readonly struct DungeonRosterBand
    {
        public readonly double Low;
        public readonly double High;

        public DungeonRosterBand(double low, double high)
        {
            Low = Sanitize(low, DungeonRosterSelector.BandLow, DungeonRosterSelector.MaxBandLow);
            High = Sanitize(high, DungeonRosterSelector.BandHigh, DungeonRosterSelector.MaxBandHigh);
        }

        private static double Sanitize(double value, double fallback, double ceiling)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;

            return Math.Min(value, ceiling);
        }

        /// <summary>The compiled defaults, unsanitized (they already are) and built straight from the constants.</summary>
        public static DungeonRosterBand Default => new DungeonRosterBand(DungeonRosterSelector.BandLow, DungeonRosterSelector.BandHigh);
    }

    /// <summary>
    /// Family and population selection for a run, pure. Reads the World Events species tables
    /// (Content/events/axes/species) and the World Events band math, so a level-120 gem draws the same
    /// creatures a level-120 world event would (TECH-DESIGN 3.4). Species roles: 0 trash, 1 elite, 2 champion,
    /// 3 named boss - role 3 is never drawn here, exactly as WorldEventRosterSelector.NamedBossRole documents.
    /// The live weenie level comes from <c>levelOf</c>; the file's level field is informational.
    /// </summary>
    public static class DungeonRosterSelector
    {
        public const double BandLow = 1.0;

        /// <summary>
        /// The high edge multiplier for the roster band, narrowed from 1.5 to 1.15 (owner ruling, 2026-09-08).
        ///
        /// World Events feeds this same arithmetic (via WorldEventRosterSelector.TrashBand/EliteBand) the
        /// MEDIAN LEVEL of a player AUDIENCE, and 1.0-1.5x is the right spread when the thing being sized is a
        /// crowd - a 275-strong turnout has plenty of players well above its own median, and the band is meant
        /// to reach them. Threads feeds the identical arithmetic the level PRINTED ON THE GEM, which a player
        /// reads as the dungeon's own advertised level, not as a median of anything. At the old 1.5x spread a
        /// level-185 gem drew creatures up to level 278 - a run whose gem says "Level: 185" fielding a monster
        /// nearly 100 levels above that number reads as the gem lying about its own difficulty, not as
        /// variety. 1.15x keeps that same gem's ceiling at 213, a spread a player can read off the gem and
        /// still recognise as "roughly this level".
        ///
        /// Still not clamped to WorldEventRosterSelector.MaxConsideredLevel (275) - that reasoning is
        /// unchanged and lives on <see cref="TrashBand"/>: 275 is a bound on a player audience level, not on a
        /// monster level, and monsters above 275 are shipped and roster-reachable.
        /// </summary>
        public const double BandHigh = 1.15;

        /// <summary>
        /// Typo ceilings for <see cref="BandLow"/>/<see cref="BandHigh"/> when either is overridden by a live
        /// PropertyManager tunable (see <see cref="DungeonRosterBand"/>). These are NOT a statement about what
        /// a good spread is - the shipped values are 1.0 and 1.15, and anything up to these ceilings is a
        /// legitimate tuning choice the ceiling deliberately does not judge. They exist because both edges are
        /// LIVE, editable with one /pm command and with no content-lint gate in front of them, and a
        /// fat-fingered or copy-pasted value should not be able to turn the band into something absurd - 5x
        /// the gem level on either edge is far past any plausible tuning and still short of drawing from the
        /// entire creature roster regardless of the gem's own level. Raise them if a real design ever wants
        /// more; do not remove them.
        /// </summary>
        public const double MaxBandLow = 5.0;
        public const double MaxBandHigh = 5.0;

        public const int PreferredFamilyWeight = 3;

        /// <summary>
        /// How far the band's LOW edge may step down, as a fraction of the gem level, when the natural band's
        /// pool is too thin to field a varied run (dynamic_dungeons_band_low_floor). 1.0 disables the whole
        /// adaptive-band feature: the low edge never moves, nothing is ever tagged for uplift, and a run is
        /// byte-for-byte what it was before this feature existed.
        ///
        /// The problem this exists for is measured, not theoretical. Drawing by LIVE weenie level from the
        /// World Events species tables, a level-275 gem's natural band holds 17 role-0 members across 6
        /// families, and per-family depth is far worse than that total suggests: the `elemental` family has
        /// exactly ONE in-band trash member and `virindi` has exactly one, so a run that rolls either fills
        /// every spawn point in the dungeon with copies of a single wcid. 0.60 lets a level-275 gem reach
        /// down to 165, where the pool is several hundred deep, and the creatures reached that way are
        /// normalized back up to what the natural band actually carries (see <see cref="DungeonBandStandard"/>)
        /// rather than being dropped in as the under-levelled monsters they were authored as.
        ///
        /// The HIGH edge never moves under any of this. It is the edge a player reads off the gem as the
        /// dungeon's advertised difficulty (see <see cref="BandHigh"/>), and widening it upward would field
        /// monsters above what the gem promises - which is the exact complaint the 1.15x narrowing fixed.
        /// </summary>
        public const double BandLowFloor = 0.60;

        /// <summary>
        /// How much the low edge's ratio drops per widening iteration. Small enough that a band stops at the
        /// first width that actually satisfies the threshold rather than overshooting into a much easier
        /// pool, and large enough that the worst case (1.0 down to the 0.60 floor) is 8 steps rather than a
        /// long scan. Not a tunable: it is the resolution of a search, not a design choice, and an admin who
        /// wants a different outcome has the floor and the two thresholds to move.
        /// </summary>
        public const double BandLowStep = 0.05;

        /// <summary>
        /// How many species tables must be able to field at least one in-band trash member before the band
        /// stops widening (dynamic_dungeons_min_eligible_families). 0 or less disables family widening.
        ///
        /// FAMILIES, not members, because family count is what a player experiences as variety across runs:
        /// two gems of the same level that both roll the only eligible family produce two visually identical
        /// dungeons no matter how many members that family has. 4 is the smallest count at which the
        /// preferred-family weighting (<see cref="PreferredFamilyWeight"/>) still has losers to choose
        /// between.
        /// </summary>
        public const int MinEligibleFamilies = 4;

        /// <summary>
        /// How many DISTINCT trash wcids the chosen family's pool must hold before its own band stops
        /// widening (dynamic_dungeons_min_family_pool). 0 or less disables per-family widening.
        ///
        /// This is the half of the feature that fixes the sharper measured fault: family eligibility only
        /// requires ONE in-band member, so a run can pass the family threshold and still stand the same wcid
        /// on all 40 of a dungeon's spawn points. 6 is chosen against the shipped dungeon sizes - the curated
        /// point counts run from roughly a dozen upward, so 6 distinct wcids is the point at which a room no
        /// longer reads as a cloned pack.
        /// </summary>
        public const int MinFamilyPool = 6;

        /// <summary>
        /// The band edges, [floor(level * low), ceil(level * high)], with the same rounding rule
        /// WorldEventRosterSelector uses: the low edge floors and the high edge ceilings, so the integer band
        /// CONTAINS the real-valued interval rather than clipping a member sitting just inside it.
        ///
        /// The one and only difference from WorldEventRosterSelector.TrashBand/EliteBand is that the high
        /// edge is NOT clamped to WorldEventRosterSelector.MaxConsideredLevel (275), and that is the whole
        /// reason this arithmetic is duplicated here instead of being borrowed. See <see cref="TrashBand"/>.
        ///
        /// No multiplier validation here: the caller (TrashBand/EliteBand) already resolved low/high through
        /// <see cref="DungeonRosterBand"/>, which sanitizes a live-tunable value before it ever reaches this
        /// function. The inverted-band guard is kept anyway, because it costs one compare and it is what makes
        /// the function total for any future constant.
        /// </summary>
        private static LevelBand Band(int level, double low, double high)
        {
            var clamped = Math.Max(0, level);

            var lowEdge = (int)Math.Floor(clamped * low);
            var highEdge = (int)Math.Ceiling(clamped * high);

            if (highEdge < lowEdge)
                highEdge = lowEdge;

            return new LevelBand(lowEdge, highEdge);
        }

        /// <summary>
        /// Trash band for a gem level. Threads owns its own band arithmetic rather than calling
        /// WorldEventRosterSelector.TrashBand, and that is deliberate.
        ///
        /// MaxConsideredLevel (275) exists to bound a PLAYER AUDIENCE level: its doc comment ties it to the
        /// client's own level chart and to the fact that no character on this fork exceeds 275. None of that
        /// reasoning applies to choosing a MONSTER, and monsters above 275 plainly exist in the tables - 24
        /// roster-reachable creatures sit at 280, 285, 300 and 320. Inheriting the audience clamp made the
        /// high edge of a level-275 gem's band collapse onto 275 itself, and once the only roster member at
        /// exactly 275 (the non-attacking Frozen Gearknight, removed in this same change) was gone, a 275 gem
        /// found NO eligible family at all and DungeonPopulationBuilder opened it with no trash whatsoever -
        /// gutting the top Raw Fragment rung. Unclamped, a 275 gem draws from [275, 413].
        ///
        /// World Events is untouched by this: WorldEventRosterSelector still clamps, nothing outside
        /// ACE.Server.ThreadDungeons calls into this class, and the two now simply disagree on purpose.
        /// </summary>
        public static LevelBand TrashBand(int level, DungeonRosterBand? band = null)
        {
            var b = band ?? DungeonRosterBand.Default;
            return Band(level, b.Low, b.High);
        }

        /// <summary>
        /// Elite / champion band for a gem level. Same proportions and the same unclamped high edge as
        /// <see cref="TrashBand"/>, and it preserves the World Events narrow-band rule: a band narrower than
        /// WorldEventRosterSelector.NarrowEliteBandFallbackWidth is not worth treating as its own band and
        /// falls back to the trash band, so a low-level gem never loses its elites to a collapsed range.
        ///
        /// With BandLow/BandHigh identical for both bands the fallback is value-identical to the band it
        /// replaces today; it is kept structurally so the rule still holds if the elite proportions are ever
        /// widened away from the trash ones, exactly as they are in World Events (1.25/1.75).
        /// </summary>
        public static LevelBand EliteBand(int level, DungeonRosterBand? band = null)
        {
            var b = band ?? DungeonRosterBand.Default;
            var raw = Band(level, b.Low, b.High);

            if (raw.High - raw.Low < WorldEventRosterSelector.NarrowEliteBandFallbackWidth)
                return TrashBand(level, b);

            return raw;
        }

        private static bool IsTrash(SpeciesMemberDef m) => m != null && m.Role == 0;
        private static bool IsElite(SpeciesMemberDef m) => m != null && (m.Role == 1 || m.Role == 2);

        private static List<uint> InBand(IEnumerable<SpeciesMemberDef> members, Func<SpeciesMemberDef, bool> role, LevelBand band, Func<uint, int> levelOf)
            => members.Where(role).Select(m => m.Wcid).Where(w => band.Contains(levelOf(w))).Distinct().ToList();

        public static bool IsEligible(SpeciesTableDef table, int level, Func<uint, int> levelOf, DungeonRosterBand? band = null)
        {
            var b = TrashBand(level, band);
            return InBand(table.Members, IsTrash, b, levelOf).Count > 0;
        }

        /// <summary>
        /// The median authored health of the trash pool a level-<paramref name="level"/> gem draws from, for
        /// the non-boss health floor (owner ruling: floor trash health to the band standard before the gem's
        /// own multipliers are applied).
        ///
        /// The pool is CROSS-FAMILY, not the run's own family: every role-0 (trash) member of EVERY table in
        /// <paramref name="tables"/> whose LIVE level (<paramref name="levelOf"/>) falls in
        /// <see cref="TrashBand"/>, distinct by wcid, exactly as <see cref="InBand"/> does for a single
        /// family. This is load-bearing, not an arbitrary simplification: any one family's own in-band pool
        /// can be thin (a handful of members) and skew far off the band's true standard in EITHER direction -
        /// e.g. the armoredillo species table's in-band trash median at the level-225 rung is 1915 hp (4
        /// members) against a cross-family median of 6200 hp for that same band, verified against the local
        /// world DB - so a family-scoped median would floor armoredillo's trash far below what every other
        /// eligible family in the band actually carries, while doing nothing for the worst case this change
        /// exists to fix. A single CP-loaded outlier member can just as easily pull a thin family's own median
        /// far ABOVE the cross-family standard instead, which would produce an oversized floor with no
        /// principled reason - either way, no individual family's sample is a reliable stand-in for the band's
        /// standard, only the pooled cross-family sample is large enough to trust. "Band standard" means the
        /// standard of the BAND every eligible family draws from, not of whichever family a given run happens
        /// to roll.
        ///
        /// <paramref name="healthOf"/> is read once per distinct wcid. A member reporting 0 is EXCLUDED from
        /// the sample, not counted as a zero-health data point: 0 means "no authored health data" (see
        /// ThreadDungeonSpawner.LiveHealthOf, which returns 0 when either attribute dictionary is missing),
        /// and a weenie missing data is not evidence the band's standard is low.
        ///
        /// Returns the MEDIAN, never the mean: an integer selected from the sample is reproducible from the
        /// same inputs without floating-point drift, which matters because this floor is logged and reasoned
        /// about by admins reading server output. For an EVEN sample count this returns the LOWER of the two
        /// middle values (never averaged) - same reproducibility reasoning, and it means the floor never
        /// climbs past the band's true midpoint on a coin flip.
        ///
        /// Returns 0 for an empty pool (no in-band trash member with usable health data anywhere in
        /// <paramref name="tables"/>), which every caller must read as "no floor" - the same convention
        /// <see cref="PickFamily"/> uses for "no family".
        /// </summary>
        public static uint BandMedianHealth(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, Func<uint, uint> healthOf, DungeonRosterBand? band = null)
        {
            var b = TrashBand(level, band);

            var wcids = tables.Values
                .SelectMany(t => t.Members)
                .Where(IsTrash)
                .Select(m => m.Wcid)
                .Where(w => b.Contains(levelOf(w)))
                .Distinct()
                .ToList();

            return LowerMedian(wcids.Select(healthOf).Where(h => h > 0));
        }

        /// <summary>
        /// The median of a sample, taking the LOWER of the two middle values for an even count and never
        /// averaging them. 0 for an empty sample, which every caller reads as "no data".
        ///
        /// Split out of <see cref="BandMedianHealth"/> so <see cref="DungeonBandStandard"/>'s four axes use
        /// the identical statistic rather than a restatement of it - the band standard and the health floor
        /// have to be derivable from the same sample by the same rule, or an admin comparing two logged
        /// numbers is comparing two different definitions of "median". The reasoning for lower-of-the-middles
        /// rather than an average lives on BandMedianHealth: an integer selected FROM the sample is
        /// reproducible from the same inputs without floating-point drift, which matters because these
        /// numbers are logged and reasoned about by admins reading server output.
        /// </summary>
        public static uint LowerMedian(IEnumerable<uint> values)
        {
            if (values == null)
                return 0;

            var sorted = values.OrderBy(v => v).ToList();

            if (sorted.Count == 0)
                return 0;

            // Index (count - 1) / 2 is the true median for an odd count and the LOWER middle for an even one.
            return sorted[(sorted.Count - 1) / 2];
        }

        // ---- adaptive band widening ----------------------------------------------------------------------

        /// <summary>
        /// The sequence of low-edge RATIOS a widening search walks: <paramref name="startLow"/> first (so a
        /// caller that never widens still evaluates the natural band exactly once), then down in steps of
        /// <see cref="BandLowStep"/>, ending on <paramref name="floorRatio"/> exactly rather than stepping
        /// past it. Yields only <paramref name="startLow"/> when the floor is at or above it, which is what
        /// makes a floor of 1.0 a total no-op against the shipped 1.0 low edge.
        ///
        /// The final value is snapped to the floor rather than allowed to land at floor - epsilon, so the
        /// widest band a run can draw from is exactly the configured floor and not a hair below it; without
        /// that snap a floor of 0.60 against a 1.0 start would end at 0.6000000000000001 and the widest band
        /// would silently be one level narrower than configured at some gem levels.
        ///
        /// Total for any inputs: the step is a positive constant and the loop is additionally bounded by
        /// <see cref="MaxWideningSteps"/>, so a caller cannot hang on a garbled ratio that slipped past its
        /// own sanitizing.
        /// </summary>
        internal static IEnumerable<double> LowEdgeLadder(double startLow, double floorRatio)
        {
            yield return startLow;

            if (double.IsNaN(floorRatio) || double.IsNaN(startLow) || floorRatio >= startLow)
                yield break;

            var ratio = startLow;

            for (var step = 0; step < MaxWideningSteps; step++)
            {
                ratio -= BandLowStep;

                if (ratio <= floorRatio + 1e-9)
                {
                    yield return floorRatio;
                    yield break;
                }

                yield return ratio;
            }

            yield return floorRatio;
        }

        /// <summary>
        /// Hard bound on <see cref="LowEdgeLadder"/>. The shipped worst case is 8 steps (1.0 down to 0.60);
        /// the arithmetic worst case a sanitized dial can produce is 100 (the <see cref="MaxBandLow"/> ceiling
        /// of 5.0 down to a floor just above 0). This exists so the loop is provably finite for ANY inputs,
        /// including a limits object built directly in code that skipped the reader's sanitizing.
        /// </summary>
        private const int MaxWideningSteps = 128;

        /// <summary>Same band with a different LOW edge and the SAME high edge. The high edge never moves.</summary>
        private static DungeonRosterBand WithLow(DungeonRosterBand band, double low) => new DungeonRosterBand(low, band.High);

        /// <summary>The integer low edge of the NATURAL band - the threshold below which a pick is uplifted.</summary>
        public static int NaturalLowEdge(int level, DungeonRosterBand? band = null) => TrashBand(level, band).Low;

        /// <summary>How many tables can field at least one in-band trash member at <paramref name="band"/>.</summary>
        public static int EligibleFamilyCount(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, DungeonRosterBand? band = null)
            => tables == null ? 0 : tables.Values.Count(t => t != null && IsEligible(t, level, levelOf, band));

        /// <summary>
        /// The band <see cref="PickFamily"/> should select from: the natural band when it already fields
        /// <paramref name="minEligibleFamilies"/> tables, otherwise the first widened band that does, and
        /// failing that the widest band the floor allows.
        ///
        /// Returns the WIDEST band tried when the threshold is never met, rather than falling back to the
        /// natural one. That is the point of the feature: a gem level whose natural band can field one family
        /// is exactly the case a player experiences as every run looking identical, and handing back the
        /// natural band there would make the widening inert precisely where it is needed most.
        ///
        /// Nothing about <see cref="PickFamily"/> itself changes - not its ordinal ordering, not the
        /// <see cref="PreferredFamilyWeight"/> 3:1 weighting, not its throw-on-unsatisfiable-forced-family
        /// behaviour. It simply sees a different band.
        /// </summary>
        public static DungeonRosterBand ResolveEligibilityBand(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, DungeonRosterBand natural, double lowFloorRatio, int minEligibleFamilies)
        {
            if (tables == null || minEligibleFamilies <= 0)
                return natural;

            var widest = natural;

            foreach (var low in LowEdgeLadder(natural.Low, lowFloorRatio))
            {
                widest = WithLow(natural, low);

                if (EligibleFamilyCount(tables, level, levelOf, widest) >= minEligibleFamilies)
                    return widest;
            }

            return widest;
        }

        /// <summary>
        /// The band the chosen family's TRASH pool should be drawn from: <paramref name="start"/> when that
        /// already yields <paramref name="minFamilyPool"/> distinct wcids, otherwise the first widened band
        /// that does, and failing that the widest band the floor allows.
        ///
        /// <paramref name="start"/> is the band the family was SELECTED at
        /// (<see cref="ResolveEligibilityBand"/>), never the natural band, and that is load-bearing: a family
        /// that only became eligible because of the eligibility widening may have no in-band member at all at
        /// the natural band, so restarting the search from the natural band could hand back a band whose pool
        /// is empty and open the run with no trash.
        /// </summary>
        public static DungeonRosterBand ResolveFamilyTrashBand(SpeciesTableDef family, int level, Func<uint, int> levelOf,
            DungeonRosterBand start, double lowFloorRatio, int minFamilyPool)
        {
            if (family == null || minFamilyPool <= 0)
                return start;

            var widest = start;

            foreach (var low in LowEdgeLadder(start.Low, lowFloorRatio))
            {
                widest = WithLow(start, low);

                if (InBand(family.Members, IsTrash, TrashBand(level, widest), levelOf).Count >= minFamilyPool)
                    return widest;
            }

            return widest;
        }

        /// <summary>
        /// The band the chosen family's ELITE pool should be drawn from. Widened only while the elite pool is
        /// EMPTY, never to reach a member count: an elite slot is a fraction of the run
        /// (DungeonPopulationBuilder.DefaultEliteShare is 0.15), so a two-member elite pool is not the
        /// same-monster-everywhere fault that <see cref="ResolveFamilyTrashBand"/> exists to fix, and
        /// widening it to six would reach much further down than the run needs.
        ///
        /// The existing last-resort rule is untouched and still sits behind this: a family with no elite in
        /// band at any width falls back to its trash pool (<see cref="PoolFor"/>), keeping the elite ROLE and
        /// only substituting the wcid.
        /// </summary>
        public static DungeonRosterBand ResolveFamilyEliteBand(SpeciesTableDef family, int level, Func<uint, int> levelOf,
            DungeonRosterBand start, double lowFloorRatio)
        {
            if (family == null)
                return start;

            var widest = start;

            foreach (var low in LowEdgeLadder(start.Low, lowFloorRatio))
            {
                widest = WithLow(start, low);

                if (InBand(family.Members, IsElite, EliteBand(level, widest), levelOf).Count > 0)
                    return widest;
            }

            return widest;
        }

        public static SpeciesTableDef PickFamily(IReadOnlyDictionary<string, SpeciesTableDef> tables, string requestedFamily,
            IReadOnlyList<string> dungeonFamilies, IReadOnlyList<string> dungeonCreatureTypes, int level, Func<uint, int> levelOf, Random rng,
            DungeonRosterBand? band = null)
        {
            if (!string.IsNullOrEmpty(requestedFamily) && requestedFamily != DungeonGemSpec.Any)
            {
                if (!tables.TryGetValue(requestedFamily, out var forced))
                    throw new ArgumentException($"unknown family '{requestedFamily}'", nameof(requestedFamily));
                if (!IsEligible(forced, level, levelOf, band))
                    throw new ArgumentException($"family '{requestedFamily}' has no trash member in the level-{level} band", nameof(requestedFamily));
                return forced;
            }

            var eligible = tables.Values.Where(t => IsEligible(t, level, levelOf, band)).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            if (eligible.Count == 0)
                return null;

            int WeightOf(SpeciesTableDef t)
            {
                var preferred = dungeonFamilies.Contains(t.Id)
                    || (!string.IsNullOrEmpty(t.CreatureType) && dungeonCreatureTypes.Any(c => string.Equals(c, t.CreatureType, StringComparison.OrdinalIgnoreCase)));
                return preferred ? PreferredFamilyWeight : 1;
            }

            var total = eligible.Sum(WeightOf);
            var roll = rng.Next(total);
            foreach (var t in eligible)
            {
                roll -= WeightOf(t);
                if (roll < 0) return t;
            }
            return eligible[eligible.Count - 1];
        }

        /// <summary>
        /// The wcids <see cref="PickPopulation"/> would draw from for one role, as a standalone query. Same
        /// construction and the same trash fallback for an empty elite pool, so a caller that needs to
        /// re-draw a single slot (the builder's I5 duplicate swap) cannot drift from what was drawn
        /// originally. Empty when the family has no trash member in band, which is the same condition under
        /// which PickPopulation places nothing at all.
        /// </summary>
        public static List<uint> PoolFor(SpeciesTableDef family, int level, DungeonRole role, Func<uint, int> levelOf, DungeonRosterBand? band = null)
        {
            if (family == null)
                return new List<uint>();

            var trashPool = InBand(family.Members, IsTrash, TrashBand(level, band), levelOf);

            if (role == DungeonRole.Trash || trashPool.Count == 0)
                return trashPool;

            var elitePool = ElitePoolFor(family, level, levelOf, band);

            return elitePool.Count > 0 ? elitePool : trashPool;
        }

        /// <summary>
        /// The family's elite/champion members in band, with NO trash fallback - the pure query
        /// <see cref="PoolFor"/> layers its fallback on top of.
        ///
        /// Exposed because the adaptive band resolves a SEPARATE widened band per role, and the fallback has
        /// to be taken against the TRASH band's pool rather than the elite band's own trash members. Going
        /// through PoolFor there would take the fallback at the elite band, which when a family has no elite
        /// at any width is the widest band the floor allows - so the elite slots would quietly draw from a
        /// deeper pool than the trash slots in the same run.
        /// </summary>
        public static List<uint> ElitePoolFor(SpeciesTableDef family, int level, Func<uint, int> levelOf, DungeonRosterBand? band = null)
            => family == null ? new List<uint>() : InBand(family.Members, IsElite, EliteBand(level, band), levelOf);

        /// <summary>
        /// The family's own candidate for the boss anchor, used when no curated bosses.json row is eligible
        /// (owner ruling 2026-09-06: the curated pool stays a PREFERENCE, and the guarantee that a boss
        /// outranks its pack is carried by the uplift the spawner applies, not by the pool it came from).
        ///
        /// Draws from the same pool an ELITE slot would draw from - the elite band, falling back to the trash
        /// pool when the family has no elite in band - and returns the highest-level member of it, because a
        /// promoted boss should at least be the family's best showing before the uplift is layered on.
        ///
        /// <paramref name="alreadyDrawn"/> is invariant I5: a wcid the pack already contains is skipped when
        /// anything else is available, so the boss is not a renamed copy of a monster standing next door.
        /// Ties on level are broken by <paramref name="rng"/> over a wcid-ordered list, so the choice is
        /// deterministic for a given gem seed. Returns 0 when the family can field nothing.
        /// </summary>
        public static uint PickBossCandidate(SpeciesTableDef family, int level, Func<uint, int> levelOf, Random rng,
            IReadOnlyCollection<uint> alreadyDrawn, DungeonRosterBand? band = null)
        {
            return PickBossCandidate(PoolFor(family, level, DungeonRole.Elite, levelOf, band), levelOf, rng, alreadyDrawn);
        }

        /// <summary>
        /// <see cref="PickBossCandidate(SpeciesTableDef, int, Func{uint, int}, Random, IReadOnlyCollection{uint}, DungeonRosterBand?)"/>'s
        /// core, over a pool the caller already resolved. Split out so the adaptive band can hand in a pool
        /// drawn at a WIDENED elite band without this function needing to know how that band was chosen - and
        /// so the band-based overload above and the widened path provably share one selection rule rather
        /// than two that can drift.
        /// </summary>
        public static uint PickBossCandidate(IReadOnlyList<uint> pool, Func<uint, int> levelOf, Random rng,
            IReadOnlyCollection<uint> alreadyDrawn)
        {
            if (pool == null || pool.Count == 0)
                return 0;

            // Prefer an unused member; fall back to the whole pool rather than returning nothing, since a
            // one-member family still needs a boss (I5 explicitly only binds when there is a choice).
            var fresh = alreadyDrawn == null || alreadyDrawn.Count == 0
                ? pool
                : pool.Where(w => !alreadyDrawn.Contains(w)).ToList();

            var candidates = fresh.Count > 0 ? fresh : pool;

            var best = candidates.Max(levelOf);
            var top = candidates.Where(w => levelOf(w) == best).OrderBy(w => w).ToList();

            return top[rng.Next(top.Count)];
        }

        public static List<DungeonRosterPick> PickPopulation(SpeciesTableDef family, int level, int slotCount, double eliteShare, Func<uint, int> levelOf, Random rng,
            DungeonRosterBand? band = null)
        {
            if (family == null)
                return new List<DungeonRosterPick>();

            return PickPopulation(PoolFor(family, level, DungeonRole.Trash, levelOf, band),
                PoolFor(family, level, DungeonRole.Elite, levelOf, band), slotCount, eliteShare, rng);
        }

        /// <summary>
        /// <see cref="PickPopulation(SpeciesTableDef, int, int, double, Func{uint, int}, Random, DungeonRosterBand?)"/>'s
        /// core, over pools the caller already resolved. Split out for the adaptive band, which resolves a
        /// separate widened band per role (see <see cref="ResolveFamilyTrashBand"/> and
        /// <see cref="ResolveFamilyEliteBand"/>) and so cannot express its two pools as one band.
        ///
        /// The draw order and the rng consumption are IDENTICAL to the band-based path - one
        /// <c>rng.Next(pool.Count)</c> per slot, elites first - which is what makes a run whose band never
        /// widened bit-identical to the same run before this feature existed.
        /// </summary>
        public static List<DungeonRosterPick> PickPopulation(IReadOnlyList<uint> trashPool, IReadOnlyList<uint> elitePool,
            int slotCount, double eliteShare, Random rng)
        {
            var picks = new List<DungeonRosterPick>();

            if (trashPool == null || trashPool.Count == 0 || slotCount <= 0)
                return picks;

            // The last-resort elite rule, restated here so a caller handing in a genuinely empty elite pool
            // gets the same substitution PoolFor performs: the elite ROLE survives, only the wcid falls back.
            if (elitePool == null || elitePool.Count == 0)
                elitePool = trashPool;

            eliteShare = Math.Clamp(double.IsNaN(eliteShare) ? 0 : eliteShare, 0, 1);
            var eliteCount = (int)Math.Floor(slotCount * eliteShare);
            if (slotCount > 1)
                eliteCount = Math.Min(eliteCount, slotCount - 1);

            for (var i = 0; i < slotCount; i++)
            {
                var elite = i < eliteCount;
                var pool = elite ? elitePool : trashPool;
                picks.Add(new DungeonRosterPick(pool[rng.Next(pool.Count)], elite ? DungeonRole.Elite : DungeonRole.Trash));
            }

            return picks;
        }
    }
}
