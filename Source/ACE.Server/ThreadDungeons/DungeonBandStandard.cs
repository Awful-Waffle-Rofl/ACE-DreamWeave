using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
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
    /// Health is deliberately NOT an axis here. DungeonSpawnPlan.TrashHealthFloor already floors every
    /// non-boss creature's health to the cross-family band median times its own ratio, and it applies to an
    /// uplifted entry unchanged; a second health path would double-apply and would need its own tunable to
    /// disagree with.
    ///
    /// KNOWN LIMITATION, and it is a property of the data rather than of this code: the highest authored
    /// level on any roster-reachable member is 320, so at gem levels near the top of the ladder the natural
    /// band's sample is drawn from an increasingly small and increasingly fixed set of high-level creatures,
    /// and the standard FLATTENS - a level-275 gem and a level-265 gem end up normalizing to nearly the same
    /// numbers. That is accepted. No extrapolation curve is invented here, because a curve fitted past the
    /// end of the authored data would be a guess presented as a measurement, and the whole point of this type
    /// is that every number it produces was read off a creature the band can really draw.
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
            int spellTier, int sampleCount, double sampleLowRatio)
        {
            SkillMedians = skillMedians ?? new Dictionary<Skill, uint>();
            MaxBodyDamage = maxBodyDamage;
            MaxBaseArmor = maxBaseArmor;
            SpellTier = spellTier;
            SampleCount = sampleCount;
            SampleLowRatio = sampleLowRatio;
        }

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
        /// </summary>
        public static DungeonBandStandard Compute(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, DungeonRosterBand natural, double lowFloorRatio, int minSample = MinSample)
        {
            if (tables == null || profileOf == null)
                return Empty;

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
                sample = SampleAt(tables, level, Cached, new DungeonRosterBand(candidateLow, natural.High));

                if (sample.Count >= minSample)
                    break;
            }

            if (sample == null || sample.Count == 0)
                return Empty;

            return new DungeonBandStandard(SkillMediansOf(sample),
                DungeonRosterSelector.LowerMedian(sample.Select(p => p.MaxBodyDamage).Where(v => v > 0)),
                DungeonRosterSelector.LowerMedian(sample.Select(p => p.MaxBaseArmor).Where(v => v > 0)),
                (int)DungeonRosterSelector.LowerMedian(sample.Select(p => (uint)p.MaxSpellTier).Where(v => v > 0)),
                sample.Count, lowRatio);
        }

        /// <summary>
        /// The distinct role-0 members of every table whose live level falls in <paramref name="band"/>'s
        /// trash band and whose profile carries at least one usable data point, in wcid-stable order.
        /// </summary>
        private static List<DungeonStatProfile> SampleAt(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, DungeonRosterBand band)
        {
            var b = DungeonRosterSelector.TrashBand(level, band);

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
    }
}
