using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The roster's archetypes (<see cref="ArchetypeClassifier"/>), one classification per wcid, memoised. Each creature
    /// is classified at its OWN authored level against the cross-archetype band standard there
    /// (<see cref="DungeonBandStandard.Compute"/> plus <see cref="DungeonRosterSelector.BandMedianHealth"/>, both over
    /// the same tables, roster band and band low floor) - the construction archetype-census uses, so the runtime and the
    /// census agree on every wcid given the same band and floor.
    ///
    /// UNPROJECTED by design: a creature's archetype is a property of what it is, not of the run it was drawn into, so
    /// the profile delegate here is the raw one even when a reach-up run samples projected profiles.
    ///
    /// A pure function of (tables, band, floor, profile delegate, formula resolver); the memos only make repeat questions
    /// cheap, and both are concurrent so a cached instance can be shared across plan builds (ThreadPlanCache keys it on
    /// the tables reference, the band and the floor).
    /// </summary>
    public sealed class RosterArchetypes
    {
        private readonly IReadOnlyDictionary<string, SpeciesTableDef> tables;
        private readonly DungeonRosterBand band;
        private readonly double lowFloor;
        private readonly Func<uint, DungeonStatProfile> profileOf;
        private readonly Func<Skill, SkillFormula> formulaOf;
        private readonly ConcurrentDictionary<int, ArchetypeBandStandard> standards = new ConcurrentDictionary<int, ArchetypeBandStandard>();
        private readonly ConcurrentDictionary<uint, ArchetypeResult> results = new ConcurrentDictionary<uint, ArchetypeResult>();
        private readonly ConcurrentDictionary<uint, DungeonStatProfile> profiles = new ConcurrentDictionary<uint, DungeonStatProfile>();

        public RosterArchetypes(IReadOnlyDictionary<string, SpeciesTableDef> tables, DungeonRosterBand band, double lowFloor,
            Func<uint, DungeonStatProfile> profileOf, Func<Skill, SkillFormula> formulaOf = null)
        {
            this.tables = tables;
            this.band = band;
            this.lowFloor = lowFloor;
            this.profileOf = profileOf;
            this.formulaOf = formulaOf;
        }

        /// <summary>The raw (authored) profile this classifier read for <paramref name="wcid"/>; never null.</summary>
        public DungeonStatProfile ProfileOf(uint wcid)
            => profiles.GetOrAdd(wcid, w => profileOf?.Invoke(w) ?? DungeonStatProfile.Empty);

        /// <summary>The cross-archetype standard the melee split divides by, at one level.</summary>
        public ArchetypeBandStandard StandardAt(int level)
            => standards.GetOrAdd(level, l =>
            {
                var standard = DungeonBandStandard.Compute(tables, l, ProfileOf, band, lowFloor, formulaOf: formulaOf);
                var health = DungeonRosterSelector.BandMedianHealth(tables, l, w => ProfileOf(w).Level, w => ProfileOf(w).Health, band);

                return ArchetypeBandStandard.From(standard, health);
            });

        /// <summary>The classification of one wcid at its own authored level.</summary>
        public ArchetypeResult Classify(uint wcid)
            => results.GetOrAdd(wcid, w =>
            {
                var profile = ProfileOf(w);
                return ArchetypeClassifier.Classify(profile, StandardAt(profile.Level), formulaOf ?? DungeonBandStandard.DefaultFormulaOf);
            });

        private readonly ConcurrentDictionary<CreatureArchetype, int> minLevels = new ConcurrentDictionary<CreatureArchetype, int>();

        /// <summary>
        /// The lowest authored level of any role-0 member of <paramref name="archetype"/> (0 when it has none): the floor
        /// of the ratio-transfer walk, which never looks below the archetype's own population.
        /// </summary>
        public int MinLevelOf(CreatureArchetype archetype)
            => minLevels.GetOrAdd(archetype, a =>
            {
                var min = 0;

                foreach (var wcid in tables.Values.Where(t => t?.Members != null).SelectMany(t => t.Members)
                             .Where(m => m != null && m.Role == 0).Select(m => m.Wcid).Distinct())
                {
                    var level = ProfileOf(wcid).Level;

                    if (level > 0 && Classify(wcid).Archetype == a && (min == 0 || level < min))
                        min = level;
                }

                return min;
            });
    }

    /// <summary>
    /// One archetype's standard at one level: the band-standard axes over that archetype's own sample
    /// (<see cref="Core"/>, reduced by exactly DungeonBandStandard's medians) plus the archetype uplift's extra axes.
    /// Immutable. A value of 0 on an axis means "no data", the subsystem-wide convention.
    /// </summary>
    public sealed class ArchetypeAxes
    {
        public static readonly ArchetypeAxes Empty = new ArchetypeAxes(DungeonBandStandard.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 1.0);

        public ArchetypeAxes(DungeonBandStandard core, double effectiveDamage, double spellOutput, double damageRating, double damageResistRating,
            double critRating, double critDamageRating, double health, int sampleCount, double sampleLowRatio,
            IReadOnlyList<(Skill Skill, uint Effective)> attackSkillSample = null,
            IReadOnlyList<double> resistMedians = null, IReadOnlyList<double> armorModMedians = null)
        {
            ResistMedians = resistMedians;
            ArmorModMedians = armorModMedians;
            Core = core ?? DungeonBandStandard.Empty;
            EffectiveDamage = effectiveDamage;
            SpellOutput = spellOutput;
            DamageRating = damageRating;
            DamageResistRating = damageResistRating;
            CritRating = critRating;
            CritDamageRating = critDamageRating;
            Health = health;
            SampleCount = sampleCount;
            SampleLowRatio = sampleLowRatio;
            AttackSkillSample = attackSkillSample ?? Array.Empty<(Skill, uint)>();
        }

        /// <summary>
        /// Every sample member's EFFECTIVE value of every attack skill it carries (attribute term + authored InitLevel,
        /// DungeonBandStandard's effective-median inclusion rule), all schools pooled, as measured (never projected).
        /// The attack-skill ceiling (<see cref="DungeonArchetypeStandards.AttackSkillCeiling"/>) is a percentile of it.
        /// </summary>
        public IReadOnlyList<(Skill Skill, uint Effective)> AttackSkillSample { get; }

        /// <summary>The DungeonBandStandard axes (skills, effective skills, attributes, body damage and armour, spell tier).</summary>
        public DungeonBandStandard Core { get; }

        /// <summary>Lower median of the sample's <see cref="ArchetypeFacts.EffectiveMaxDamage"/> (0 excluded).</summary>
        public double EffectiveDamage { get; }

        /// <summary>Lower median of the sample's <see cref="ArchetypeFacts.SpellOutput"/> over the members that carry a war/void projectile.</summary>
        public double SpellOutput { get; }

        /// <summary>
        /// The four rating standards: the MEAN over the whole sample, INCLUDING zeros (owner default 2026-10-23 - a rating
        /// most creatures do not carry is a small standard, not a missing one; a median would read 0 for nearly every
        /// archetype and floor nothing).
        /// </summary>
        public double DamageRating { get; }
        public double DamageResistRating { get; }
        public double CritRating { get; }
        public double CritDamageRating { get; }

        /// <summary>
        /// Health. As measured: the lower median of the sample's authored health (0 excluded). As resolved for a run by
        /// <see cref="ArchetypeUpliftContext"/>: the health STANDARD H_A(level) = T(level) x H_A / H_All on the health
        /// curve (DungeonHealthCurve), or the measured median when the curve is off.
        /// </summary>
        public double Health { get; }

        public int SampleCount { get; }

        /// <summary>
        /// The sample's UPPER-middle ResistX per <see cref="ArchetypeFacts.ResistProperties"/> entry (absent = 1.0), over the
        /// members whose facts carry resists: the SOFTER middle (a higher resist multiplier is less resistant), so a floor
        /// at it never asks more of an uplifted creature than the median natural one carries. Dimensionless, so it is
        /// carried FLAT up the stat curve and copied (never ratio-scaled) by a ratio transfer. Null, or a 0 entry, means
        /// no data (the uplift resist floor then writes nothing for that element).
        /// </summary>
        public IReadOnlyList<double> ResistMedians { get; }

        /// <summary>
        /// The sample's LOWER-middle ArmorModVsX per <see cref="ArchetypeFacts.ArmorModProperties"/> entry (absent = 1.0), over
        /// the members with body armour (MaxBaseArmor above 0) whose facts carry them: again the softer middle. Null, or a
        /// 0 entry, means no data.
        /// </summary>
        public IReadOnlyList<double> ArmorModMedians { get; }

        /// <summary>The low-edge ratio this archetype's own sample widened to (the natural low edge when it did not).</summary>
        public double SampleLowRatio { get; }

        public ArchetypeAxes With(DungeonBandStandard core = null, double? effectiveDamage = null, double? spellOutput = null, double? health = null)
            => new ArchetypeAxes(core ?? Core, effectiveDamage ?? EffectiveDamage, spellOutput ?? SpellOutput, DamageRating, DamageResistRating,
                CritRating, CritDamageRating, health ?? Health, SampleCount, SampleLowRatio, AttackSkillSample, ResistMedians, ArmorModMedians);
    }

    /// <summary>
    /// The per-archetype standards at ONE level (design steps 8-9, owner design 2026-10-23): one natural-band sample walk
    /// over every role-0 member of every table, partitioned by each member's archetype (<see cref="RosterArchetypes"/>).
    ///
    /// Each archetype widens its OWN sample's low edge down DungeonRosterSelector's 0.05 ladder (to the band low floor)
    /// until it holds <see cref="DungeonBandStandard.MinSample"/> members, exactly as DungeonBandStandard.Compute widens
    /// the cross-archetype sample; an archetype still under MinSample is not ADEQUATE, and a consumer falls back
    /// (<see cref="FallbackChain"/>): a Hybrid to its primary mode's archetype, then everyone to <see cref="All"/>, the
    /// cross-archetype standard - which is DungeonBandStandard.Compute's own result over the same inputs, so the "All"
    /// fallback is the band standard a Threads run already normalizes to.
    ///
    /// DungeonBandStandard.Compute is not changed (World Events share it); this type only re-uses its sample walk and its
    /// reduction (DungeonBandStandard.FromSample).
    /// </summary>
    public sealed class DungeonArchetypeStandards
    {
        /// <summary>The five archetypes that get a standard of their own.</summary>
        public static readonly CreatureArchetype[] Partitioned =
        {
            CreatureArchetype.Caster, CreatureArchetype.Ranged, CreatureArchetype.MeleeTank, CreatureArchetype.MeleeDps, CreatureArchetype.Hybrid,
        };

        private readonly IReadOnlyDictionary<CreatureArchetype, ArchetypeAxes> byArchetype;

        private DungeonArchetypeStandards(int level, ArchetypeAxes all, IReadOnlyDictionary<CreatureArchetype, ArchetypeAxes> byArchetype, int minSample)
        {
            Level = level;
            All = all ?? ArchetypeAxes.Empty;
            this.byArchetype = byArchetype ?? new Dictionary<CreatureArchetype, ArchetypeAxes>();
            MinSample = minSample;
        }

        public static readonly DungeonArchetypeStandards Empty = new DungeonArchetypeStandards(0, ArchetypeAxes.Empty, null, DungeonBandStandard.MinSample);

        public int Level { get; }

        public int MinSample { get; }

        /// <summary>The cross-archetype standard (every archetype's members together).</summary>
        public ArchetypeAxes All { get; }

        /// <summary>
        /// The standard of one source: an archetype's own, or <see cref="All"/> for <see cref="CreatureArchetype.Unclassified"/>
        /// (the chain's "everyone" link). Never null; an archetype with no members reads <see cref="ArchetypeAxes.Empty"/>.
        /// </summary>
        public ArchetypeAxes Get(CreatureArchetype source)
            => source == CreatureArchetype.Unclassified ? All : byArchetype.TryGetValue(source, out var axes) ? axes : ArchetypeAxes.Empty;

        /// <summary>
        /// Whether a source can stand as a standard: an archetype with at least <see cref="MinSample"/> members after its
        /// widening, or a non-empty <see cref="All"/>.
        /// </summary>
        public bool IsAdequate(CreatureArchetype source)
        {
            var axes = Get(source);

            return source == CreatureArchetype.Unclassified ? !axes.Core.IsEmpty : axes.SampleCount >= MinSample && !axes.Core.IsEmpty;
        }

        /// <summary>
        /// The order a creature's standard is looked for in (pure): its own archetype; for a Hybrid, then its primary
        /// mode's archetype; then everyone (Unclassified, read as <see cref="All"/>). An Unclassified creature goes
        /// straight to everyone.
        /// </summary>
        public static IReadOnlyList<CreatureArchetype> FallbackChain(CreatureArchetype archetype, CreatureArchetype primary)
        {
            var chain = new List<CreatureArchetype>();

            if (archetype != CreatureArchetype.Unclassified)
                chain.Add(archetype);

            if (archetype == CreatureArchetype.Hybrid && primary != CreatureArchetype.Hybrid && primary != CreatureArchetype.Unclassified)
                chain.Add(primary);

            chain.Add(CreatureArchetype.Unclassified);

            return chain;
        }

        /// <summary>
        /// The first source in <paramref name="chain"/> adequate at BOTH ends (pure), so a creature's own-level and run-level
        /// standards always come from the SAME population and the ratio between them means something; null when not even
        /// the cross-archetype standard is available at both.
        /// </summary>
        public static CreatureArchetype? CommonSource(IReadOnlyList<CreatureArchetype> chain, DungeonArchetypeStandards run, DungeonArchetypeStandards own)
        {
            foreach (var source in chain)
                if (run.IsAdequate(source) && own.IsAdequate(source))
                    return source;

            return null;
        }

        /// <summary>
        /// Measures the standards at <paramref name="level"/> over <paramref name="natural"/>. <paramref name="profileOf"/>
        /// is the SAMPLE's profile delegate (projected when a reach-up run measures at its own level); the archetype of each
        /// member always comes from <paramref name="roster"/>, unprojected.
        /// </summary>
        public static DungeonArchetypeStandards Measure(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, RosterArchetypes roster, DungeonRosterBand natural, double lowFloorRatio,
            int minSample = DungeonBandStandard.MinSample, Func<Skill, SkillFormula> formulaOf = null)
        {
            if (tables == null || profileOf == null || roster == null)
                return Empty;

            var memo = new Dictionary<uint, DungeonStatProfile>();

            DungeonStatProfile Cached(uint wcid)
            {
                if (!memo.TryGetValue(wcid, out var p))
                    memo[wcid] = p = profileOf(wcid) ?? DungeonStatProfile.Empty;

                return p;
            }

            // The cross-archetype standard: DungeonBandStandard.Compute's own walk, reproduced through its two extracted
            // halves so the per-archetype samples below can be cut from the SAME member lists.
            var ladder = DungeonRosterSelector.LowEdgeLadder(natural.Low, lowFloorRatio).ToList();
            var samples = new List<(double Low, List<(uint Wcid, DungeonStatProfile Profile)> Members)>();

            foreach (var candidateLow in ladder)
            {
                var b = DungeonRosterSelector.TrashBand(level, new DungeonRosterBand(candidateLow, natural.High));

                var members = tables.Values
                    .Where(t => t?.Members != null)
                    .SelectMany(t => t.Members)
                    .Where(m => m != null && m.Role == 0)
                    .Select(m => m.Wcid)
                    .Distinct()
                    .OrderBy(w => w)
                    .Select(w => (Wcid: w, Profile: Cached(w)))
                    .Where(x => b.Contains(x.Profile.Level) && x.Profile.HasData)
                    .ToList();

                samples.Add((candidateLow, members));
            }

            ArchetypeAxes Reduce(Func<uint, bool> include)
            {
                (double Low, List<DungeonStatProfile> Sample) chosen = (natural.Low, new List<DungeonStatProfile>());

                foreach (var (low, members) in samples)
                {
                    chosen = (low, members.Where(m => include(m.Wcid)).Select(m => m.Profile).ToList());

                    if (chosen.Sample.Count >= minSample)
                        break;
                }

                return AxesOf(chosen.Sample, chosen.Low, formulaOf);
            }

            var all = Reduce(w => true);
            var byArchetype = new Dictionary<CreatureArchetype, ArchetypeAxes>();

            foreach (var archetype in Partitioned)
                byArchetype[archetype] = Reduce(w => roster.Classify(w).Archetype == archetype);

            return new DungeonArchetypeStandards(level, all, byArchetype, minSample);
        }

        /// <summary>The axes over one chosen sample (pure).</summary>
        internal static ArchetypeAxes AxesOf(List<DungeonStatProfile> sample, double lowRatio, Func<Skill, SkillFormula> formulaOf)
        {
            if (sample == null || sample.Count == 0)
                return new ArchetypeAxes(DungeonBandStandard.Empty, 0, 0, 0, 0, 0, 0, 0, 0, lowRatio);

            return new ArchetypeAxes(
                DungeonBandStandard.FromSample(sample, lowRatio, formulaOf),
                DungeonRosterSelector.LowerMedian(sample.Select(p => p.Archetype.EffectiveMaxDamage).Where(v => v > 0)),
                DungeonBandStandard.LowerMedian(sample.Select(p => p.Archetype.SpellOutput).Where(v => v > 0)),
                sample.Average(p => (double)p.Archetype.DamageRating),
                sample.Average(p => (double)p.Archetype.DamageResistRating),
                sample.Average(p => (double)p.Archetype.CritRating),
                sample.Average(p => (double)p.Archetype.CritDamageRating),
                DungeonRosterSelector.LowerMedian(sample.Select(p => p.Health).Where(v => v > 0)),
                sample.Count, lowRatio,
                AttackSkillSampleOf(sample, formulaOf),
                ResistMediansOf(sample), ArmorModMediansOf(sample));
        }

        /// <summary>
        /// The upper-middle ResistX per element over the members whose facts carry resists (pure); null when none does.
        /// Upper middle: index count / 2 of the ascending values, the softer of the two middles.
        /// </summary>
        internal static double[] ResistMediansOf(IReadOnlyList<DungeonStatProfile> sample)
        {
            var members = (sample ?? Array.Empty<DungeonStatProfile>()).Where(p => p?.Archetype?.Resists != null).Select(p => p.Archetype.Resists).ToList();

            if (members.Count == 0)
                return null;

            var medians = new double[ArchetypeFacts.ResistProperties.Length];

            for (var i = 0; i < medians.Length; i++)
            {
                var sorted = members.Select(r => r[i]).OrderBy(v => v).ToList();
                medians[i] = sorted[sorted.Count / 2];
            }

            return medians;
        }

        /// <summary>
        /// The lower-middle ArmorModVsX per element over the members with body armour whose facts carry them (pure); null
        /// when none does. Lower middle: index (count - 1) / 2 of the ascending values, the softer of the two middles.
        /// </summary>
        internal static double[] ArmorModMediansOf(IReadOnlyList<DungeonStatProfile> sample)
        {
            var members = (sample ?? Array.Empty<DungeonStatProfile>()).Where(p => p != null && p.MaxBaseArmor > 0 && p.Archetype?.ArmorMods != null)
                .Select(p => p.Archetype.ArmorMods).ToList();

            if (members.Count == 0)
                return null;

            var medians = new double[ArchetypeFacts.ArmorModProperties.Length];

            for (var i = 0; i < medians.Length; i++)
            {
                var sorted = members.Select(r => r[i]).OrderBy(v => v).ToList();
                medians[i] = sorted[(sorted.Count - 1) / 2];
            }

            return medians;
        }

        /// <summary>
        /// Every member's effective attack-skill values (pure): attribute term + authored InitLevel for each
        /// <see cref="DungeonCombatNormalizer.AttackSkills"/> entry it carries with a non-zero InitLevel, members with no
        /// attribute rows excluded - the exact inclusion rule of DungeonBandStandard's effective medians.
        /// </summary>
        internal static List<(Skill Skill, uint Effective)> AttackSkillSampleOf(List<DungeonStatProfile> sample, Func<Skill, SkillFormula> formulaOf)
        {
            var values = new List<(Skill, uint)>();

            if (sample == null)
                return values;

            formulaOf ??= DungeonBandStandard.DefaultFormulaOf;

            foreach (var profile in sample)
            {
                if (profile == null || profile.Attributes.Count == 0)
                    continue;

                foreach (var skill in DungeonCombatNormalizer.AttackSkills)
                {
                    if (!profile.Skills.TryGetValue(skill, out var initLevel) || initLevel == 0)
                        continue;

                    var formula = formulaOf(skill);

                    if (formula == null)
                        continue;

                    values.Add((skill, AttributeFormula.Compute(formula, attr => profile.Attributes.TryGetValue(attr, out var v) ? v : 0u) + initLevel));
                }
            }

            return values;
        }

        /// <summary>The percentile of natural effective attack skill the attack-skill ceiling sits at (orchestrator ruling 2026-10-23: P75).</summary>
        public const double AttackSkillCeilingQuantile = 0.75;

        /// <summary>
        /// The ATTACK-SKILL CEILING (pure, orchestrator ruling 2026-10-23): the <paramref name="quantile"/> (nearest rank)
        /// of the pooled effective attack-skill values of the natural band, each carried to the run level by
        /// <paramref name="ratioOf"/> (its own axis's stat-curve ratio; 1 when the run is measured where it is placed).
        /// An absolute value, no rho: ratio transfer and the k term must not invent attack skill above what natural
        /// creatures at the run level carry. 0 (no ceiling) for an empty sample.
        /// </summary>
        public static uint AttackSkillCeiling(IEnumerable<(Skill Skill, uint Effective)> sample, Func<Skill, double> ratioOf, double quantile = AttackSkillCeilingQuantile)
        {
            if (sample == null)
                return 0;

            var values = sample
                .Select(s =>
                {
                    var ratio = ratioOf?.Invoke(s.Skill) ?? 1.0;
                    return double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0 ? s.Effective : DungeonStatCurve.Scale(s.Effective, ratio);
                })
                .Where(v => v > 0)
                .OrderBy(v => v)
                .ToList();

            if (values.Count == 0)
                return 0;

            var q = double.IsNaN(quantile) ? AttackSkillCeilingQuantile : Math.Clamp(quantile, 0.0, 1.0);
            var rank = (int)Math.Ceiling(q * values.Count);

            return values[Math.Clamp(rank - 1, 0, values.Count - 1)];
        }

        /// <summary>
        /// One archetype's measured standard at the pivot carried up the stat curve to <paramref name="level"/> (pure): the
        /// band-standard axes by <see cref="DungeonStatCurve.Extrapolate"/> (so magic defense is the retail curve above 300),
        /// effective damage by the curve's Damage ratio, SPELL OUTPUT and the ratings FLAT at the pivot standard, and health
        /// left as measured (the context prices health on the health curve). Spell output is flat by orchestrator ruling
        /// 2026-10-23: a natural reach-up-stamped pick never has its spells scaled (ReachUpStamp.Apply touches attributes,
        /// skills, body parts and weapons only), so a curve-carried output standard would overshoot every natural pick.
        /// </summary>
        public static ArchetypeAxes Extrapolate(ArchetypeAxes measured, int pivot, int level, double defenseRate, double magicDefenseRate, double damageRate,
            Func<Skill, SkillFormula> formulaOf = null)
        {
            if (measured == null || measured.Core.IsEmpty || pivot <= 0 || level <= pivot)
                return measured ?? ArchetypeAxes.Empty;

            var damageRatio = DungeonStatCurve.Ratio(DungeonStatAxis.Damage, pivot, level, defenseRate, magicDefenseRate, damageRate, pivot);

            return measured.With(
                core: DungeonStatCurve.Extrapolate(measured.Core, pivot, level, defenseRate, magicDefenseRate, damageRate, formulaOf),
                effectiveDamage: measured.EffectiveDamage * damageRatio);
        }

        /// <summary>The level step of the ratio-transfer walk below a thin band (orchestrator ruling 2026-10-23).</summary>
        public const int TransferStep = 25;

        /// <summary>
        /// RATIO TRANSFER (pure, orchestrator ruling 2026-10-23): an archetype too thin to stand as a standard at some level
        /// takes the cross-archetype standard there, <paramref name="allTarget"/>, times its OWN ratio to everyone measured
        /// where it is adequate: S_A = (S_A(ref) / S_All(ref)) x S_All, per axis. An axis the archetype does not carry at the
        /// reference (a skill none of its members has, a 0 median) is dropped, not borrowed from everyone; an axis everyone
        /// lacks at the target stays absent. The spell tier is the target's own tier when the archetype casts tierable
        /// spells at the reference, else 0. The sample count and low edge are the reference's, so the result reads adequate.
        /// </summary>
        public static ArchetypeAxes TransferRatio(ArchetypeAxes allTarget, ArchetypeAxes archetypeRef, ArchetypeAxes allRef)
        {
            if (allTarget == null || archetypeRef == null || allRef == null)
                return ArchetypeAxes.Empty;

            double Ratio(double a, double all) => a > 0 && all > 0 ? a / all : 0.0;
            double Scale(double target, double a, double all) => target * Ratio(a, all);
            uint ScaleU(uint target, uint a, uint all) => (uint)Math.Clamp(Math.Round(Scale(target, a, all), MidpointRounding.AwayFromZero), 0, uint.MaxValue);

            Dictionary<TKey, uint> Map<TKey>(IReadOnlyDictionary<TKey, uint> target, IReadOnlyDictionary<TKey, uint> a, IReadOnlyDictionary<TKey, uint> all)
            {
                var result = new Dictionary<TKey, uint>();

                foreach (var kvp in target)
                {
                    if (!a.TryGetValue(kvp.Key, out var av) || !all.TryGetValue(kvp.Key, out var allv))
                        continue;

                    var v = ScaleU(kvp.Value, av, allv);

                    if (v > 0)
                        result[kvp.Key] = v;
                }

                return result;
            }

            var t = allTarget.Core;
            var ar = archetypeRef.Core;
            var lr = allRef.Core;

            var core = DungeonBandStandard.Create(
                Map(t.SkillMedians, ar.SkillMedians, lr.SkillMedians),
                ScaleU(t.MaxBodyDamage, ar.MaxBodyDamage, lr.MaxBodyDamage),
                ScaleU(t.MaxBaseArmor, ar.MaxBaseArmor, lr.MaxBaseArmor),
                ar.SpellTier > 0 ? t.SpellTier : 0,
                archetypeRef.SampleCount, archetypeRef.SampleLowRatio,
                Scale(t.HealthRate, ar.HealthRate, lr.HealthRate),
                Map(t.AttributeMedians, ar.AttributeMedians, lr.AttributeMedians),
                Map(t.EffectiveSkillMedians, ar.EffectiveSkillMedians, lr.EffectiveSkillMedians));

            return new ArchetypeAxes(core,
                Scale(allTarget.EffectiveDamage, archetypeRef.EffectiveDamage, allRef.EffectiveDamage),
                Scale(allTarget.SpellOutput, archetypeRef.SpellOutput, allRef.SpellOutput),
                Scale(allTarget.DamageRating, archetypeRef.DamageRating, allRef.DamageRating),
                Scale(allTarget.DamageResistRating, archetypeRef.DamageResistRating, allRef.DamageResistRating),
                Scale(allTarget.CritRating, archetypeRef.CritRating, allRef.CritRating),
                Scale(allTarget.CritDamageRating, archetypeRef.CritDamageRating, allRef.CritDamageRating),
                Scale(allTarget.Health, archetypeRef.Health, allRef.Health),
                archetypeRef.SampleCount, archetypeRef.SampleLowRatio,
                allTarget.AttackSkillSample,
                archetypeRef.ResistMedians, archetypeRef.ArmorModMedians);
        }
    }

    /// <summary>
    /// What one uplifted creature is normalized against: its archetype and the standards at both ends - the run level L
    /// and its own authored level l - from ONE common source (<see cref="DungeonArchetypeStandards.CommonSource"/>), plus
    /// the run's ratios. Immutable; built by <see cref="ArchetypeUpliftContext.For"/>.
    /// </summary>
    public sealed class ArchetypeUpliftInfo
    {
        public uint Wcid { get; set; }
        public CreatureArchetype Archetype { get; set; }
        public CreatureArchetype PrimaryArchetype { get; set; }

        /// <summary>The population both standards were read from: the archetype itself, a Hybrid's primary, or Unclassified for everyone.</summary>
        public CreatureArchetype Source { get; set; }

        public int RunLevel { get; set; }
        public int OwnLevel { get; set; }

        /// <summary>S_A(L): the source's standard at the run level (extrapolated above the pivot), health on the curve.</summary>
        public ArchetypeAxes Run { get; set; }

        /// <summary>S_A(l): the source's standard at the creature's own authored level, unprojected, health on the curve.</summary>
        public ArchetypeAxes Own { get; set; }

        /// <summary>The cross-archetype standards at L and l: magic defense reads these (no archetype factor, owner default).</summary>
        public DungeonBandStandard AllRun { get; set; }
        public DungeonBandStandard AllOwn { get; set; }

        /// <summary>The creature's own authored effective max damage (weenie level, ArchetypeFacts.EffectiveMaxDamage).</summary>
        public uint OwnEffectiveDamage { get; set; }

        public double OffenseRatio { get; set; } = 1.0;
        public double DefenseRatio { get; set; } = 1.0;

        /// <summary>dynamic_dungeons_uplift_archetype_cap as the plan read it: the OFFENSE cap (0 = no cap).</summary>
        public double CapRatio { get; set; }

        /// <summary>
        /// dynamic_dungeons_uplift_defense_cap as the plan read it: the DEFENSE cap, the headroom over rho_def x S_A(L) the
        /// defensive axes may keep (1.0 = exactly the median, the shipped default; 0 = no cap).
        /// </summary>
        public double DefenseCapRatio { get; set; } = DungeonPopulationLimits.DefaultUpliftDefenseCap;

        /// <summary>
        /// Where each end's standard came from: 0 = measured directly at that end's own band; otherwise the RATIO-TRANSFER
        /// reference level (the highest level, stepping down from that end, where the source archetype was adequate).
        /// </summary>
        public int RunSourceLevel { get; set; }
        public int OwnSourceLevel { get; set; }

        /// <summary>
        /// The ratio-transfer inputs of each end, for tools and logs (null when that end was measured directly): the
        /// source archetype's standard and everyone's at the reference level, so r = archetype / all per axis.
        /// </summary>
        public ArchetypeAxes RunTransferArchetype { get; set; }
        public ArchetypeAxes RunTransferAll { get; set; }
        public ArchetypeAxes OwnTransferArchetype { get; set; }
        public ArchetypeAxes OwnTransferAll { get; set; }

        /// <summary>
        /// The attack-skill ceiling (<see cref="DungeonArchetypeStandards.AttackSkillCeiling"/>): no EFFECTIVE attack-skill
        /// target is written above it (but never below the creature's own value). 0 = no ceiling.
        /// </summary>
        public uint AttackSkillCeiling { get; set; }

        /// <summary>Sample sizes and source bands, for log lines and tools.</summary>
        public string SourceDescription
            => $"n {Run?.SampleCount}@L{(RunSourceLevel > 0 ? $" via ratio at l{RunSourceLevel}" : "")} " +
               $"{Own?.SampleCount}@l{OwnLevel}{(OwnSourceLevel > 0 ? $" via ratio at l{OwnSourceLevel}" : "")}";
    }

    /// <summary>
    /// The plan's archetype-uplift state (DungeonSpawnPlan.ArchetypeUplift): the measured run-level standards and the
    /// resolvers for own-level standards, plus a per-wcid cache of <see cref="ArchetypeUpliftInfo"/>. The builder fills
    /// the cache for every uplifted entry, so the placement path only reads it; an offline tool can ask about any wcid.
    /// </summary>
    public sealed class ArchetypeUpliftContext
    {
        private readonly DungeonArchetypeStandards runMeasured;
        private readonly Func<int, DungeonArchetypeStandards> measureOwn;
        private readonly Func<int, DungeonArchetypeStandards> measureRunReference;
        private readonly RosterArchetypes roster;
        private readonly Func<int, double> healthCurve;
        private readonly Func<Skill, SkillFormula> formulaOf;
        private readonly ConcurrentDictionary<uint, ArchetypeUpliftInfo> infos = new ConcurrentDictionary<uint, ArchetypeUpliftInfo>();
        private readonly ConcurrentDictionary<CreatureArchetype, ArchetypeAxes> runResolved = new ConcurrentDictionary<CreatureArchetype, ArchetypeAxes>();

        /// <param name="runMeasured">The standards measured at <paramref name="referenceLevel"/>: the pivot when the stat curve applies, else the run level.</param>
        /// <param name="extrapolate">True when the run is above the pivot with the stat curve on: every run-level axis is carried up from the pivot.</param>
        /// <param name="retailMagicDefense">dynamic_dungeons_stat_curve: magic defense standards follow the retail curve above level 300.</param>
        /// <param name="healthCurve">DungeonHealthCurve.Target at a level, 0 when the curve is off.</param>
        /// <param name="measureRunReference">
        /// The standards the RUN end's ratio transfer reads its reference band from; null reads <paramref name="measureOwn"/>.
        /// The builder passes a natural-band-only measurement (orchestrator ruling 2026-10-23, option a), so the band low
        /// floor moves neither S_A(L) nor the reference it is transferred from.
        /// </param>
        /// <param name="defenseRate">dynamic_dungeons_defense_curve_rate_above_375: the melee/missile softening above 300.</param>
        /// <param name="magicDefenseRate">dynamic_dungeons_magic_defense_curve_rate_above_300: the retail magic curve's rate above 300.</param>
        /// <param name="damageRate">dynamic_dungeons_damage_curve_rate_above_pivot: the Damage axis's rate above the pivot.</param>
        public ArchetypeUpliftContext(int runLevel, int referenceLevel, bool extrapolate, double defenseRate, double magicDefenseRate, double damageRate, bool retailMagicDefense,
            DungeonArchetypeStandards runMeasured, Func<int, DungeonArchetypeStandards> measureOwn, RosterArchetypes roster,
            Func<int, double> healthCurve, double offenseRatio, double defenseRatio, double capRatio, Func<Skill, SkillFormula> formulaOf = null,
            Func<int, DungeonArchetypeStandards> measureRunReference = null, double defenseCapRatio = DungeonPopulationLimits.DefaultUpliftDefenseCap)
        {
            this.measureRunReference = measureRunReference ?? measureOwn;
            DefenseCapRatio = defenseCapRatio;
            RunLevel = runLevel;
            ReferenceLevel = referenceLevel;
            Extrapolated = extrapolate;
            DefenseRate = defenseRate;
            MagicDefenseRate = magicDefenseRate;
            DamageRate = damageRate;
            RetailMagicDefense = retailMagicDefense;
            this.runMeasured = runMeasured ?? DungeonArchetypeStandards.Empty;
            this.measureOwn = measureOwn;
            this.roster = roster;
            this.healthCurve = healthCurve ?? (l => 0.0);
            OffenseRatio = offenseRatio;
            DefenseRatio = defenseRatio;
            CapRatio = capRatio;
            this.formulaOf = formulaOf;
        }

        public int RunLevel { get; }
        public int ReferenceLevel { get; }
        public bool Extrapolated { get; }
        public double DefenseRate { get; }
        public double MagicDefenseRate { get; }

        /// <summary>dynamic_dungeons_damage_curve_rate_above_pivot: the Damage axis's rate above the pivot (<see cref="ReferenceLevel"/> when extrapolated).</summary>
        public double DamageRate { get; }
        public bool RetailMagicDefense { get; }
        public double OffenseRatio { get; }
        public double DefenseRatio { get; }

        /// <summary>dynamic_dungeons_uplift_archetype_cap: the OFFENSE cap (0 = no cap).</summary>
        public double CapRatio { get; }

        /// <summary>dynamic_dungeons_uplift_defense_cap: the DEFENSE cap, headroom over the median (1.0 = exactly the median; 0 = no cap).</summary>
        public double DefenseCapRatio { get; }

        /// <summary>The measured run-level standards (at <see cref="ReferenceLevel"/>), for logs and tools.</summary>
        public DungeonArchetypeStandards RunMeasured => runMeasured;

        /// <summary>The classifier this context reads archetypes from.</summary>
        public RosterArchetypes Roster => roster;

        /// <summary>One source's standard at the run level, resolved (extrapolated or retail-magic-adjusted, health on the curve). Memoised.</summary>
        public ArchetypeAxes RunAxes(CreatureArchetype source)
            => runResolved.GetOrAdd(source, s =>
            {
                var measured = runMeasured.Get(s);
                var resolved = Extrapolated
                    ? DungeonArchetypeStandards.Extrapolate(measured, ReferenceLevel, RunLevel, DefenseRate, MagicDefenseRate, DamageRate, formulaOf)
                    : RetailMagicDefense ? measured.With(core: DungeonStatCurve.WithRetailMagicDefense(measured.Core, RunLevel, MagicDefenseRate, formulaOf)) : measured;

                return resolved.With(health: HealthStandard(RunLevel, measured.Health, runMeasured.All.Health));
            });

        /// <summary>
        /// The health standard at one level (pure): on the health curve, T(level) x the source's measured median over the
        /// cross-archetype median at the level it was measured (so an archetype keeps its relative toughness); the measured
        /// median itself when the curve is off (T = 0) or either median is missing.
        /// </summary>
        public static double HealthOnCurve(double curveTarget, double measured, double measuredAll)
            => curveTarget > 0 && measured > 0 && measuredAll > 0 ? curveTarget * measured / measuredAll : measured;

        private double HealthStandard(int level, double measured, double measuredAll) => HealthOnCurve(healthCurve(level), measured, measuredAll);

        /// <summary>A measured source at one level, resolved the way the own end reads it (retail magic defense, health on the curve).</summary>
        private ArchetypeAxes ResolveAt(DungeonArchetypeStandards set, ArchetypeAxes measured, int level)
            => (RetailMagicDefense ? measured.With(core: DungeonStatCurve.WithRetailMagicDefense(measured.Core, level, MagicDefenseRate, formulaOf)) : measured)
                .With(health: HealthStandard(level, measured.Health, set.All.Health));

        private readonly ConcurrentDictionary<(CreatureArchetype, int), (DungeonArchetypeStandards Set, int Level)?> references =
            new ConcurrentDictionary<(CreatureArchetype, int), (DungeonArchetypeStandards, int)?>();

        /// <summary>
        /// The ratio-transfer reference for a source thin at <paramref name="fromLevel"/>: the highest level below it, in
        /// <see cref="DungeonArchetypeStandards.TransferStep"/> steps (each level's measurement walks the 0.05 low-edge
        /// ladder itself), where the source AND everyone are adequate; never below the source's lowest authored member.
        /// Null when there is none. Memoised.
        /// </summary>
        private (DungeonArchetypeStandards Set, int Level)? ReferenceFor(CreatureArchetype source, int fromLevel)
            => ReferenceIn(references, measureOwn, source, fromLevel);

        private readonly ConcurrentDictionary<(CreatureArchetype, int), (DungeonArchetypeStandards Set, int Level)?> runReferences =
            new ConcurrentDictionary<(CreatureArchetype, int), (DungeonArchetypeStandards, int)?>();

        /// <summary>The run end's reference: the same walk over <c>measureRunReference</c> (natural band only, from the builder).</summary>
        private (DungeonArchetypeStandards Set, int Level)? RunReferenceFor(CreatureArchetype source, int fromLevel)
            => ReferenceIn(runReferences, measureRunReference, source, fromLevel);

        private (DungeonArchetypeStandards Set, int Level)? ReferenceIn(
            ConcurrentDictionary<(CreatureArchetype, int), (DungeonArchetypeStandards Set, int Level)?> memo,
            Func<int, DungeonArchetypeStandards> measure, CreatureArchetype source, int fromLevel)
            => memo.GetOrAdd((source, fromLevel), key =>
            {
                var floor = roster.MinLevelOf(source);

                if (floor <= 0 || measure == null)
                    return null;

                for (var level = fromLevel - DungeonArchetypeStandards.TransferStep; level >= floor; level -= DungeonArchetypeStandards.TransferStep)
                {
                    var set = measure(level) ?? DungeonArchetypeStandards.Empty;

                    if (set.IsAdequate(source) && set.IsAdequate(CreatureArchetype.Unclassified))
                        return (set, level);
                }

                return null;
            });

        /// <summary>One end's resolved standard, where it came from (0 = direct), and, when transferred, the reference pair.</summary>
        private readonly struct End
        {
            public End(ArchetypeAxes axes, int sourceLevel = 0, ArchetypeAxes refArchetype = null, ArchetypeAxes refAll = null)
            {
                Axes = axes; SourceLevel = sourceLevel; RefArchetype = refArchetype; RefAll = refAll;
            }

            public ArchetypeAxes Axes { get; }
            public int SourceLevel { get; }
            public ArchetypeAxes RefArchetype { get; }
            public ArchetypeAxes RefAll { get; }
        }

        /// <summary>A source's run-level standard: measured directly when adequate, else ratio-transferred onto everyone's (source level 0 = direct).</summary>
        private End? RunEnd(CreatureArchetype source)
        {
            if (runMeasured.IsAdequate(source))
                return new End(RunAxes(source));

            if (!runMeasured.IsAdequate(CreatureArchetype.Unclassified))
                return null;

            var reference = RunReferenceFor(source, ReferenceLevel);

            if (!reference.HasValue)
                return null;

            var refArchetype = reference.Value.Set.Get(source);
            var refAll = reference.Value.Set.All;

            return new End(DungeonArchetypeStandards.TransferRatio(RunAxes(CreatureArchetype.Unclassified), refArchetype, refAll), reference.Value.Level, refArchetype, refAll);
        }

        /// <summary>A source's own-level standard: measured directly when adequate, else ratio-transferred onto everyone's there.</summary>
        private End? OwnEnd(CreatureArchetype source, DungeonArchetypeStandards ownSet, int ownLevel)
        {
            if (ownSet.IsAdequate(source))
                return new End(ResolveAt(ownSet, ownSet.Get(source), ownLevel));

            if (!ownSet.IsAdequate(CreatureArchetype.Unclassified))
                return null;

            var reference = ReferenceFor(source, ownLevel);

            if (!reference.HasValue)
                return null;

            var refArchetype = reference.Value.Set.Get(source);
            var refAll = reference.Value.Set.All;

            return new End(DungeonArchetypeStandards.TransferRatio(ResolveAt(ownSet, ownSet.All, ownLevel), refArchetype, refAll), reference.Value.Level, refArchetype, refAll);
        }

        private uint? attackSkillCeiling;

        /// <summary>
        /// The run's attack-skill ceiling (<see cref="DungeonArchetypeStandards.AttackSkillCeiling"/>): the P75 of the
        /// cross-archetype natural sample's pooled effective attack skill, each value carried from
        /// <see cref="ReferenceLevel"/> to <see cref="RunLevel"/> on its own axis when the run is extrapolated. Memoised.
        /// </summary>
        public uint AttackSkillCeiling
        {
            get
            {
                if (attackSkillCeiling.HasValue)
                    return attackSkillCeiling.Value;

                double RatioOf(Skill skill)
                {
                    var axis = DungeonStatCurve.AxisOf(skill);
                    return Extrapolated && axis.HasValue ? DungeonStatCurve.Ratio(axis.Value, ReferenceLevel, RunLevel, DefenseRate, MagicDefenseRate, DamageRate, ReferenceLevel) : 1.0;
                }

                var value = DungeonArchetypeStandards.AttackSkillCeiling(runMeasured.All.AttackSkillSample, RatioOf);
                attackSkillCeiling = value;

                return value;
            }
        }

        /// <summary>
        /// The uplift info for one wcid, or null when not even the cross-archetype standard has data at both ends (the
        /// creature then takes the legacy uplift path). Memoised; the builder pre-fills it for every uplifted entry.
        ///
        /// Resolution order (orchestrator rulings 2026-10-23), first match wins:
        ///   1. the creature's own archetype, measured directly at both ends;
        ///   2. for a Hybrid, its primary mode's archetype, measured directly at both ends;
        ///   3. the creature's own archetype with each end measured directly where adequate and RATIO-TRANSFERRED where
        ///      thin (<see cref="DungeonArchetypeStandards.TransferRatio"/> from the highest adequate band below);
        ///   4. for a Hybrid, its primary the same way;
        ///   5. everyone (the cross-archetype standard), which an Unclassified creature goes to directly.
        /// </summary>
        public ArchetypeUpliftInfo For(uint wcid)
        {
            if (roster == null || measureOwn == null)
                return null;

            if (infos.TryGetValue(wcid, out var cached))
                return cached;

            var profile = roster.ProfileOf(wcid);
            var result = roster.Classify(wcid);
            var ownLevel = profile.Level;

            if (ownLevel <= 0)
                return null;

            var ownSet = measureOwn(ownLevel) ?? DungeonArchetypeStandards.Empty;
            var candidates = DungeonArchetypeStandards.FallbackChain(result.Archetype, result.PrimaryArchetype)
                .Where(c => c != CreatureArchetype.Unclassified).ToList();

            CreatureArchetype? source = null;
            End? run = null, own = null;

            foreach (var candidate in candidates)
            {
                if (runMeasured.IsAdequate(candidate) && ownSet.IsAdequate(candidate))
                {
                    source = candidate;
                    run = new End(RunAxes(candidate));
                    own = new End(ResolveAt(ownSet, ownSet.Get(candidate), ownLevel));
                    break;
                }
            }

            if (!source.HasValue)
            {
                foreach (var candidate in candidates)
                {
                    var r = RunEnd(candidate);
                    var o = r.HasValue ? OwnEnd(candidate, ownSet, ownLevel) : null;

                    if (r.HasValue && o.HasValue)
                    {
                        source = candidate;
                        run = r;
                        own = o;
                        break;
                    }
                }
            }

            if (!source.HasValue && runMeasured.IsAdequate(CreatureArchetype.Unclassified) && ownSet.IsAdequate(CreatureArchetype.Unclassified))
            {
                source = CreatureArchetype.Unclassified;
                run = new End(RunAxes(CreatureArchetype.Unclassified));
                own = new End(ResolveAt(ownSet, ownSet.All, ownLevel));
            }

            if (!source.HasValue)
                return null;

            var allOwn = RetailMagicDefense ? DungeonStatCurve.WithRetailMagicDefense(ownSet.All.Core, ownLevel, MagicDefenseRate, formulaOf) : ownSet.All.Core;

            var info = new ArchetypeUpliftInfo
            {
                Wcid = wcid,
                Archetype = result.Archetype,
                PrimaryArchetype = result.PrimaryArchetype,
                Source = source.Value,
                RunLevel = RunLevel,
                OwnLevel = ownLevel,
                Run = run.Value.Axes,
                Own = own.Value.Axes,
                RunSourceLevel = run.Value.SourceLevel,
                OwnSourceLevel = own.Value.SourceLevel,
                RunTransferArchetype = run.Value.RefArchetype,
                RunTransferAll = run.Value.RefAll,
                OwnTransferArchetype = own.Value.RefArchetype,
                OwnTransferAll = own.Value.RefAll,
                AttackSkillCeiling = AttackSkillCeiling,
                AllRun = RunAxes(CreatureArchetype.Unclassified).Core,
                AllOwn = allOwn,
                OwnEffectiveDamage = profile.Archetype.EffectiveMaxDamage,
                OffenseRatio = OffenseRatio,
                DefenseRatio = DefenseRatio,
                CapRatio = CapRatio,
                DefenseCapRatio = DefenseCapRatio,
            };

            return infos.GetOrAdd(wcid, info);
        }
    }
}
