using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// What a creature drawn at a gem's NATURAL band actually carries, per axis, so a creature that only
    /// reached the run through the band's downward extension can be normalized up to it
    /// (<see cref="DungeonSpawnPlanEntry.UpliftLevel"/>).
    ///
    /// CROSS-FAMILY, and computed by exactly the same construction DungeonRosterSelector.BandMedianHealth
    /// uses for the health floor - read that method's doc comment, its reasoning is load-bearing and is not
    /// restated in full here. In short: every role-0 (trash) member of EVERY table whose LIVE level falls in
    /// the band, distinct by wcid; a value of 0 means "no authored data" and is EXCLUDED from that axis's
    /// sample rather than counted as a zero; and the statistic is the MEDIAN, taking the LOWER of the two
    /// middles for an even count, because an integer selected from the sample is reproducible from the same
    /// inputs without floating-point drift.
    ///
    /// Health is deliberately NOT an axis here, and the reason is unchanged by the 2026-09-12 health curve:
    /// the plan already carries the health terms, they already apply to an uplifted entry unchanged, and a
    /// second health path here would double-apply and would need its own tunable to disagree with. What has
    /// changed is WHICH terms those are. It is no longer the single TrashHealthFloor against the measured
    /// cross-family median; it is now DungeonSpawnPlan.HealthNormalizeRatio (every non-boss creature's
    /// authored health scaled onto <see cref="DungeonHealthCurve"/>'s smooth ladder) plus a TrashHealthFloor
    /// derived from that same curve rather than from the sample. Both are applied at the spawner to every
    /// non-boss entry including uplifted ones, so the double-application argument above still binds.
    ///
    /// KNOWN LIMITATION, and it is a property of the data rather than of this code: the authored roster is
    /// finite, and at gem levels near the top of the ladder the natural band's sample is drawn from an
    /// increasingly small and increasingly fixed set of high-level creatures, so the standard FLATTENS - two
    /// neighbouring top-of-ladder gems end up normalizing to nearly the same numbers. The roster is NOT
    /// capped at 320: the #1032 bestiary carries role-0 creatures at level 375, so a gem-375 run does sample
    /// real creatures at its own natural band (a small set, which is the flattening above, not an absent
    /// one).
    ///
    /// THIS TYPE STILL INVENTS NO CURVE, and every number it produces is still read off a creature the band
    /// can really draw. What changed on 2026-10-08 is that Threads no longer USES it above the pivot (the
    /// highest gem level whose natural band lies wholly inside the authored data, 326 on the shipped roster):
    /// the owner ruled that every monster stat follows its natural curve past the authored data, so the
    /// Threads builder measures this standard AT the pivot and hands it to <see cref="DungeonStatCurve"/>,
    /// which extrapolates it with fitted per-axis slopes. The flattening above is therefore what
    /// dynamic_dungeons_stat_curve = false falls back to, not what a run above the pivot normally sees. World
    /// Events never extrapolate and are untouched.
    /// </summary>
    public sealed class DungeonBandStandard
    {
        /// <summary>
        /// The smallest natural-band sample worth deriving a standard from. Below this the sample band's LOW
        /// edge widens (the same 0.05 stepping the draw band uses) until the threshold is met or the floor is
        /// reached. A compile-time constant rather than a tunable on purpose: it is a statistical-validity
        /// threshold, not a design dial - there is no gameplay reading of "normalize against a sample of 2"
        /// that an admin would ever want to choose, and every extra live tunable is another value the
        /// registration and the code can drift on.
        /// </summary>
        public const int MinSample = 5;

        /// <summary>Median authored InitLevel per skill, over the members of the sample that carry that skill.</summary>
        public IReadOnlyDictionary<Skill, uint> SkillMedians { get; }

        /// <summary>Median of each sample member's MAXIMUM body-part DVal (melee max damage).</summary>
        public uint MaxBodyDamage { get; }

        /// <summary>Median of each sample member's MAXIMUM body-part BaseArmor.</summary>
        public uint MaxBaseArmor { get; }

        /// <summary>
        /// Median of each sample member's <see cref="DungeonStatProfile.MaxSpellTier"/>, over only the
        /// members that carry at least one tierable spell (the same "0 excluded" convention MaxBodyDamage and
        /// MaxBaseArmor use above). 0 means no sample member carried a tierable spell, and is read by
        /// <see cref="ThreadDungeonSpawner"/> as "raise nothing" - the same no-op convention every other axis
        /// here uses for an absent standard.
        /// </summary>
        public int SpellTier { get; }

        /// <summary>
        /// Lower median of the sample's <see cref="DungeonStatProfile.HealthRate"/>, over only the members
        /// that author a positive rate (the same "0 excluded" convention as every other axis). 0 means no
        /// sample member authored one, which boss normalization reads as "leave the boss's rate alone".
        /// </summary>
        public double HealthRate { get; }

        /// <summary>
        /// Lower median of each primary attribute's authored base (<see cref="DungeonStatProfile.Attributes"/>), per
        /// attribute, over the sample members that carry a nonzero value for it - the same "0 excluded" rule
        /// SkillMedians uses. Read only by boss normalization, which SETS a boss's attributes here so its skill
        /// Current (attribute formula + InitLevel) lands on the band's, not just its InitLevel.
        /// </summary>
        public IReadOnlyDictionary<PropertyAttribute, uint> AttributeMedians { get; }

        /// <summary>
        /// Median EFFECTIVE value per attack skill (DungeonCombatNormalizer.AttackSkills) and per melee/missile/
        /// magic defense skill (DungeonCombatNormalizer.DefenseSkills)
        /// over the sample members that carry both a nonzero authored InitLevel for that skill AND at least one
        /// authored attribute row - "effective" meaning AttributeFormula's attribute-formula contribution
        /// (AttributeFormula.Compute, replicated here from each member's own authored DungeonStatProfile.Attributes
        /// rather than a live Creature) PLUS that member's own authored InitLevel, the same two terms
        /// CreatureSkill.Base/Current sum for a non-player (CreatureSkill.cs:146-168).
        ///
        /// A member with NO attribute rows at all is EXCLUDED from every skill's sample here, rather than having
        /// its attribute contribution read as 0: DungeonStatProfile.Attributes is empty exactly when the source
        /// weenie carried no PropertiesAttribute rows (ThreadDungeonSpawner.ProfileOfWeenie), which is "no data
        /// authored" on the same convention every other axis on this type uses - not "this creature's primary
        /// attributes are all zero", which would be nonsense for a living creature and would drag the effective
        /// median toward the bare InitLevel term for members whose attribute rows simply were not read.
        ///
        /// The nonzero-InitLevel exclusion is <see cref="SkillMediansOf"/>'s own rule, kept here for the same
        /// reason: a skill a member does not carry says nothing about the band's defense standard for it.
        ///
        /// Read by <see cref="DungeonCombatNormalizer.DefenseSkillCap"/> (defense skills only, through
        /// <see cref="EffectiveDefenseMedians"/>) and by the Threads uplift's effective-skill top-up
        /// (BandUplift.TopUpEffectiveSkills, attack and defense). 0 means "no standard for this skill", the same
        /// convention <see cref="MedianFor"/> uses for <see cref="SkillMedians"/>.
        /// </summary>
        public IReadOnlyDictionary<Skill, uint> EffectiveSkillMedians { get; }

        /// <summary>
        /// The defense-skill VIEW of <see cref="EffectiveSkillMedians"/>: only the entries for
        /// DungeonCombatNormalizer.DefenseSkills, so the defense ceiling reads exactly the figures it always did.
        /// </summary>
        public IReadOnlyDictionary<Skill, uint> EffectiveDefenseMedians { get; }

        /// <summary>How many distinct wcids with usable data the sample held. 0 means "no standard".</summary>
        public int SampleCount { get; }

        /// <summary>
        /// The low-edge ratio the SAMPLE band ended up at. Equal to the natural band's own low edge whenever
        /// the natural sample was already large enough; below it when the sample had to be widened. Carried
        /// for the log line, so an admin reading server output can tell a normalized run's standard was
        /// derived from a widened sample rather than the advertised band.
        /// </summary>
        public double SampleLowRatio { get; }

        private DungeonBandStandard(IReadOnlyDictionary<Skill, uint> skillMedians, uint maxBodyDamage, uint maxBaseArmor,
            int spellTier, int sampleCount, double sampleLowRatio, double healthRate = 0.0,
            IReadOnlyDictionary<PropertyAttribute, uint> attributeMedians = null,
            IReadOnlyDictionary<Skill, uint> effectiveSkillMedians = null)
        {
            SkillMedians = skillMedians ?? new Dictionary<Skill, uint>();
            MaxBodyDamage = maxBodyDamage;
            MaxBaseArmor = maxBaseArmor;
            SpellTier = spellTier;
            SampleCount = sampleCount;
            SampleLowRatio = sampleLowRatio;
            HealthRate = healthRate;
            AttributeMedians = attributeMedians ?? new Dictionary<PropertyAttribute, uint>();
            EffectiveSkillMedians = effectiveSkillMedians ?? new Dictionary<Skill, uint>();
            EffectiveDefenseMedians = EffectiveSkillMedians
                .Where(kvp => Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, kvp.Key) >= 0)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Test seam: builds a standard from explicit values, so the consumers (boss normalization, the
        /// defense-skill cap) can be unit-tested against a known standard without constructing a band sample.
        /// <paramref name="effectiveDefenseMedians"/> lets a test pin <see cref="EffectiveDefenseMedianFor"/>
        /// directly rather than deriving it from a sample of profiles, on the same "test seam supplies the
        /// consumer's input directly" contract <paramref name="skillMedians"/> already uses for InitLevel medians.
        /// <paramref name="effectiveSkillMedians"/> does the same for attack skills (and may carry defense
        /// skills too); the two are merged, with <paramref name="effectiveSkillMedians"/> winning a clash.
        /// </summary>
        internal static DungeonBandStandard ForTest(IReadOnlyDictionary<Skill, uint> skillMedians, uint maxBodyDamage, uint maxBaseArmor,
            int spellTier = 0, double healthRate = 0.0, int sampleCount = MinSample,
            IReadOnlyDictionary<PropertyAttribute, uint> attributeMedians = null,
            IReadOnlyDictionary<Skill, uint> effectiveDefenseMedians = null,
            IReadOnlyDictionary<Skill, uint> effectiveSkillMedians = null)
        {
            var merged = new Dictionary<Skill, uint>();

            if (effectiveDefenseMedians != null)
                foreach (var kvp in effectiveDefenseMedians)
                    merged[kvp.Key] = kvp.Value;

            if (effectiveSkillMedians != null)
                foreach (var kvp in effectiveSkillMedians)
                    merged[kvp.Key] = kvp.Value;

            return new DungeonBandStandard(skillMedians, maxBodyDamage, maxBaseArmor, spellTier, sampleCount, 1.0, healthRate, attributeMedians, merged);
        }

        /// <summary>
        /// Builds a standard from already-derived values. The one constructor seam for
        /// <see cref="DungeonStatCurve.Extrapolate"/>, which carries a measured standard up a fitted curve and
        /// so produces values no sample holds. Internal: a standard outside this assembly is either measured
        /// (<see cref="Compute"/>) or a test fixture (<see cref="ForTest"/>).
        /// </summary>
        internal static DungeonBandStandard Create(IReadOnlyDictionary<Skill, uint> skillMedians, uint maxBodyDamage, uint maxBaseArmor,
            int spellTier, int sampleCount, double sampleLowRatio, double healthRate,
            IReadOnlyDictionary<PropertyAttribute, uint> attributeMedians, IReadOnlyDictionary<Skill, uint> effectiveSkillMedians)
            => new DungeonBandStandard(skillMedians, maxBodyDamage, maxBaseArmor, spellTier, sampleCount, sampleLowRatio, healthRate,
                attributeMedians, effectiveSkillMedians);

        /// <summary>
        /// The "no standard" value, which every consumer must read as "normalize nothing". Returned whenever
        /// the sample is empty, and used as the plan's default so a run that never widens its band carries a
        /// standard that is provably a no-op rather than a null nobody checks.
        /// </summary>
        public static readonly DungeonBandStandard Empty = new DungeonBandStandard(null, 0, 0, 0, 0, 0.0);

        /// <summary>True when this standard can raise nothing.</summary>
        public bool IsEmpty => SampleCount == 0;

        /// <summary>The band's median InitLevel for one skill, or 0 ("no standard for this skill") when absent.</summary>
        public uint MedianFor(Skill skill) => SkillMedians.TryGetValue(skill, out var v) ? v : 0u;

        /// <summary>The band's median base for one primary attribute, or 0 ("no standard") when absent.</summary>
        public uint AttributeMedianFor(PropertyAttribute attribute) => AttributeMedians.TryGetValue(attribute, out var v) ? v : 0u;

        /// <summary>The band's median EFFECTIVE value for one defense skill, or 0 ("no standard") when absent.</summary>
        public uint EffectiveDefenseMedianFor(Skill skill) => EffectiveDefenseMedians.TryGetValue(skill, out var v) ? v : 0u;

        /// <summary>The band's median EFFECTIVE value for one attack or defense skill, or 0 ("no standard") when absent.</summary>
        public uint EffectiveSkillMedianFor(Skill skill) => EffectiveSkillMedians.TryGetValue(skill, out var v) ? v : 0u;

        /// <summary>
        /// Computes the standard for a gem level over <paramref name="natural"/>, widening the SAMPLE band's
        /// low edge (never its high edge) when the natural sample is smaller than <paramref name="minSample"/>.
        ///
        /// The widening here is the SAMPLE's own and is independent of whatever widening the DRAW band did:
        /// the two answer different questions. The draw band asks "can this run field enough variety"; the
        /// sample band asks "do I have enough observations to state what the band's standard is". A run can
        /// easily need one and not the other - a gem level with 40 in-band trash members spread over 2
        /// families widens its draw band and not its sample, and the reverse happens at the top of the ladder.
        ///
        /// Returns <see cref="Empty"/> when nothing in the band has usable data, which callers read as
        /// "normalize nothing" - the same "0 means no floor" convention BandMedianHealth uses.
        ///
        /// <paramref name="formulaOf"/> resolves a Skill to the SkillFormula EffectiveDefenseMedians replicates
        /// AttributeFormula.Compute against; trailing and optional so every pre-existing call site keeps
        /// compiling and so this stays testable without dats (a test passes its own resolver instead of the
        /// production one, which reads GameTables.SkillTable.SkillBaseHash and needs a loaded portal.dat).
        ///
        /// <paramref name="maxHighEdge"/> (default no cap) clamps the SAMPLE band's integer high edge. Threads
        /// never passes it; World Events passes its own 275 audience ceiling so its standard is measured over
        /// the band its picks are actually drawn from.
        /// </summary>
        public static DungeonBandStandard Compute(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, DungeonRosterBand natural, double lowFloorRatio, int minSample = MinSample,
            Func<Skill, SkillFormula> formulaOf = null, int maxHighEdge = int.MaxValue)
        {
            if (tables == null || profileOf == null)
                return Empty;

            formulaOf ??= DefaultFormulaOf;

            var lowRatio = natural.Low;
            List<DungeonStatProfile> sample = null;

            // Memoized for the whole search. Each widening step re-walks the SAME role-0 wcid set (only the
            // level filter moves), so without this a run that steps eight times would build eight profiles
            // per member - and a profile is the most expensive read in this subsystem, since it walks a
            // weenie's skill and body-part collections rather than pulling one scalar.
            var memo = new Dictionary<uint, DungeonStatProfile>();

            DungeonStatProfile Cached(uint wcid)
            {
                if (!memo.TryGetValue(wcid, out var profile))
                    memo[wcid] = profile = profileOf(wcid) ?? DungeonStatProfile.Empty;

                return profile;
            }

            foreach (var candidateLow in DungeonRosterSelector.LowEdgeLadder(natural.Low, lowFloorRatio))
            {
                lowRatio = candidateLow;
                sample = SampleAt(tables, level, Cached, new DungeonRosterBand(candidateLow, natural.High), maxHighEdge);

                if (sample.Count >= minSample)
                    break;
            }

            if (sample == null || sample.Count == 0)
                return Empty;

            return new DungeonBandStandard(SkillMediansOf(sample),
                DungeonRosterSelector.LowerMedian(sample.Select(p => p.MaxBodyDamage).Where(v => v > 0)),
                DungeonRosterSelector.LowerMedian(sample.Select(p => p.MaxBaseArmor).Where(v => v > 0)),
                (int)DungeonRosterSelector.LowerMedian(sample.Select(p => (uint)p.MaxSpellTier).Where(v => v > 0)),
                sample.Count, lowRatio,
                LowerMedian(sample.Select(p => p.HealthRate).Where(v => v > 0)),
                AttributeMediansOf(sample),
                EffectiveSkillMediansOf(sample, formulaOf));
        }

        /// <summary>
        /// The production formula resolver: a Skill's SkillFormula, straight off the loaded portal.dat via
        /// GameTables.SkillTable.SkillBaseHash - the exact same lookup AttributeFormula.GetFormula(Creature, ...)
        /// performs for a live creature. Null when the skill has no formula row. Internal so
        /// <see cref="DungeonStatCurve.Extrapolate"/> resolves the identical formula when it re-derives InitLevels.
        /// </summary>
        internal static SkillFormula DefaultFormulaOf(Skill skill)
            => GameTables.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase) ? skillBase.Formula : null;

        /// <summary>
        /// One median EFFECTIVE value per <see cref="DungeonCombatNormalizer.AttackSkills"/> and
        /// <see cref="DungeonCombatNormalizer.DefenseSkills"/>, over only the
        /// sample members that carry a nonzero authored InitLevel for that skill (<see cref="SkillMediansOf"/>'s
        /// own inclusion rule) AND at least one authored attribute row - see
        /// <see cref="EffectiveSkillMedians"/>'s doc comment for why a member with none is excluded rather
        /// than read as all-zero attributes.
        /// </summary>
        private static Dictionary<Skill, uint> EffectiveSkillMediansOf(List<DungeonStatProfile> sample, Func<Skill, SkillFormula> formulaOf)
        {
            var buckets = new Dictionary<Skill, List<uint>>();

            foreach (var profile in sample)
            {
                if (profile.Attributes.Count == 0)
                    continue;

                foreach (var skill in DungeonCombatNormalizer.AttackSkills.Concat(DungeonCombatNormalizer.DefenseSkills))
                {
                    if (!profile.Skills.TryGetValue(skill, out var initLevel) || initLevel == 0)
                        continue;

                    var formula = formulaOf(skill);

                    if (formula == null)
                        continue;

                    var attributeTerm = AttributeFormula.Compute(formula, attr => profile.Attributes.TryGetValue(attr, out var v) ? v : 0u);
                    var effective = attributeTerm + initLevel;

                    if (!buckets.TryGetValue(skill, out var values))
                        buckets[skill] = values = new List<uint>();

                    values.Add(effective);
                }
            }

            var medians = new Dictionary<Skill, uint>(buckets.Count);

            foreach (var kvp in buckets)
                medians[kvp.Key] = DungeonRosterSelector.LowerMedian(kvp.Value);

            return medians;
        }

        /// <summary>
        /// DungeonRosterSelector.LowerMedian's rule for a DOUBLE axis: the lower of the two middles for an even
        /// count, never an average, and 0 for an empty sample. Needed because HealthRate is authored as a
        /// float; truncating it into the uint overload would collapse every rate below 1.0 to "no data".
        /// </summary>
        internal static double LowerMedian(IEnumerable<double> values)
        {
            if (values == null)
                return 0.0;

            var sorted = values.OrderBy(v => v).ToList();

            return sorted.Count == 0 ? 0.0 : sorted[(sorted.Count - 1) / 2];
        }

        /// <summary>
        /// The distinct role-0 members of every table whose live level falls in <paramref name="band"/>'s
        /// trash band and whose profile carries at least one usable data point, in wcid-stable order.
        /// </summary>
        private static List<DungeonStatProfile> SampleAt(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, DungeonRosterBand band, int maxHighEdge = int.MaxValue)
        {
            var b = DungeonRosterSelector.TrashBand(level, band);

            // World Events caps the band's high edge at its audience level ceiling (275); Threads passes no cap.
            if (b.High > maxHighEdge)
                b = new ACE.Server.WorldEvents.LevelBand(b.Low, Math.Max(b.Low, maxHighEdge));

            return tables.Values
                .Where(t => t?.Members != null)
                .SelectMany(t => t.Members)
                .Where(m => m != null && m.Role == 0)
                .Select(m => m.Wcid)
                .Distinct()
                .OrderBy(w => w)
                .Select(w => profileOf(w) ?? DungeonStatProfile.Empty)
                .Where(p => b.Contains(p.Level) && p.HasData)
                .ToList();
        }

        /// <summary>
        /// One median per skill, over only the sample members that actually carry that skill with a nonzero
        /// InitLevel. Per-skill rather than per-sample on purpose: a band whose members are mostly melee
        /// creatures still has a meaningful War Magic standard drawn from its casters, and averaging a zero
        /// in for every non-caster would drag that standard to nothing and leave an uplifted caster throwing
        /// its authored spells at an untouched skill.
        /// </summary>
        private static Dictionary<Skill, uint> SkillMediansOf(List<DungeonStatProfile> sample)
        {
            var buckets = new Dictionary<Skill, List<uint>>();

            foreach (var profile in sample)
            {
                foreach (var kvp in profile.Skills)
                {
                    if (kvp.Value == 0)
                        continue;

                    if (!buckets.TryGetValue(kvp.Key, out var values))
                        buckets[kvp.Key] = values = new List<uint>();

                    values.Add(kvp.Value);
                }
            }

            var medians = new Dictionary<Skill, uint>(buckets.Count);

            foreach (var kvp in buckets)
                medians[kvp.Key] = DungeonRosterSelector.LowerMedian(kvp.Value);

            return medians;
        }

        /// <summary>
        /// One lower median per primary attribute, over only the sample members carrying a nonzero base for it.
        /// Same construction as <see cref="SkillMediansOf"/>.
        /// </summary>
        private static Dictionary<PropertyAttribute, uint> AttributeMediansOf(List<DungeonStatProfile> sample)
        {
            var buckets = new Dictionary<PropertyAttribute, List<uint>>();

            foreach (var profile in sample)
            {
                foreach (var kvp in profile.Attributes)
                {
                    if (kvp.Value == 0)
                        continue;

                    if (!buckets.TryGetValue(kvp.Key, out var values))
                        buckets[kvp.Key] = values = new List<uint>();

                    values.Add(kvp.Value);
                }
            }

            var medians = new Dictionary<PropertyAttribute, uint>(buckets.Count);

            foreach (var kvp in buckets)
                medians[kvp.Key] = DungeonRosterSelector.LowerMedian(kvp.Value);

            return medians;
        }
    }
}
