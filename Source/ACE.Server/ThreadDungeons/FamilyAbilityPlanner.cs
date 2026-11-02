using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.MonsterEffects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// What one drawn wcid contributes to its family's <see cref="CreatureAbilityFacts"/> beyond its predicted damage: whether
    /// it casts, and the monster-effect records its weenie authors (PropertyString 9015). <see cref="Empty"/> when unread.
    /// </summary>
    public sealed class AbilityMemberTraits
    {
        public static readonly AbilityMemberTraits Empty = new AbilityMemberTraits(false, null, 0);

        public AbilityMemberTraits(bool casts, IReadOnlyCollection<string> authoredKinds, int authoredRecordCount, double? castShare = null,
            double missileShare = 0.0)
        {
            Casts = casts;
            CastShare = castShare ?? (casts ? 1.0 : 0.0);
            MissileShare = missileShare;
            AuthoredKinds = authoredKinds ?? new string[0];
            AuthoredRecordCount = Math.Max(0, authoredRecordCount);
        }

        /// <summary>True when the creature casts a harmful spell (the caller decides how it reads that; see the spawner's LiveAbilityTraitsOf).</summary>
        public bool Casts { get; }

        /// <summary>
        /// The share of its attacks that are harmful spell casts (ArchetypeClassifier.MagicShareOf). Read only to decide how a
        /// BUDGET SETTER weights the draw, against the classifier's own thresholds. Defaults to 1 for a caster, 0 otherwise,
        /// when the caller has no share to give.
        /// </summary>
        public double CastShare { get; }

        /// <summary>P(its equipped weapon is a missile weapon) (ArchetypeClassifier.MissileShareOf); 0 when the caller has none.</summary>
        public double MissileShare { get; }

        /// <summary>The kind token of every record the weenie authors in PropertyString 9015, lower case.</summary>
        public IReadOnlyCollection<string> AuthoredKinds { get; }

        /// <summary>How many records the weenie authors (parsed records; a malformed one is not counted).</summary>
        public int AuthoredRecordCount { get; }

        /// <summary>
        /// The authored effect records off one weenie, with <paramref name="casts"/> supplied by the caller. Parses the
        /// spec with the engine's own parser, so a kind is spelled exactly as the overlay's exclusion compares it.
        /// </summary>
        public static AbilityMemberTraits FromWeenie(Weenie weenie, bool casts, double? castShare = null, double missileShare = 0.0)
        {
            if (weenie == null)
                return new AbilityMemberTraits(casts, null, 0, castShare, missileShare);

            string authored = null;
            weenie.PropertiesString?.TryGetValue(PropertyString.MonsterCombatEffects, out authored);

            if (string.IsNullOrWhiteSpace(authored))
                return new AbilityMemberTraits(casts, null, 0, castShare, missileShare);

            MonsterEffectParser.Parse(authored, out var specs, out _);

            var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in specs)
            {
                if (!string.IsNullOrWhiteSpace(spec.Kind))
                    kinds.Add(spec.Kind.ToLowerInvariant());
            }

            return new AbilityMemberTraits(casts, kinds.OrderBy(k => k, StringComparer.Ordinal).ToList(), specs.Count, castShare, missileShare);
        }
    }

    /// <summary>One distinct drawn member of the run's family, as the budget read it.</summary>
    public sealed class FamilyAbilityMember
    {
        public FamilyAbilityMember(uint wcid, DungeonRole role, int ownRating, double meleePerHit, double missilePerHit, int withheldMelee, int withheldMissile,
            bool casts = false, double? castShare = null, double missileShare = 0.0, bool gearUnknown = false)
        {
            Casts = casts;
            CastShare = castShare ?? (casts ? 1.0 : 0.0);
            MissileShare = double.IsNaN(missileShare) ? 0.0 : Math.Clamp(missileShare, 0.0, 1.0);
            GearUnknown = gearUnknown;
            Wcid = wcid;
            Role = role;
            OwnRating = ownRating;
            MeleePerHit = meleePerHit;
            MissilePerHit = missilePerHit;
            WithheldMelee = withheldMelee;
            WithheldMissile = withheldMissile;
        }

        public uint Wcid { get; }
        public DungeonRole Role { get; }

        /// <summary>R: the DamageRating the creature carries before the group bonus (authored or floored, plus the gem's).</summary>
        public int OwnRating { get; }

        /// <summary>The predicted per-hit on each axis BEFORE the ceiling (what ApplyDamageCeiling would measure).</summary>
        public double MeleePerHit { get; }
        public double MissilePerHit { get; }

        public int WithheldMelee { get; }
        public int WithheldMissile { get; }

        /// <summary>The larger axis: what this member offers the family budget.</summary>
        public int Withheld => Math.Max(WithheldMelee, WithheldMissile);

        /// <summary>The predicted per-hit on the axis <see cref="Withheld"/> came from (melee on a tie): the tie-break between budget setters.</summary>
        public double WithheldHit => WithheldMelee >= WithheldMissile ? MeleePerHit : MissilePerHit;

        /// <summary>The member casts a harmful spell (<see cref="AbilityMemberTraits.Casts"/>).</summary>
        public bool Casts { get; }

        /// <summary>The share of its attacks that are harmful casts (<see cref="AbilityMemberTraits.CastShare"/>).</summary>
        public double CastShare { get; }

        /// <summary>P(its equipped weapon is a missile weapon) (<see cref="AbilityMemberTraits.MissileShare"/>).</summary>
        public double MissileShare { get; }

        /// <summary>Its gear rolls from a wielded-treasure table the weenie read cannot see (<see cref="CreaturePerHit.GearUnknown"/>).</summary>
        public bool GearUnknown { get; }

        /// <summary>
        /// The classifier's melee mix (ArchetypeClassifier.Classify): 1 - magic - (1 - magic) x missileShare, floored at 0, and
        /// 0 for a member with no predicted melee hit.
        /// </summary>
        public double MeleeMix
        {
            get
            {
                if (!(MeleePerHit > 0))
                    return 0.0;

                var magic = Math.Clamp(double.IsNaN(CastShare) ? 0.0 : CastShare, 0.0, 1.0);
                return Math.Max(0.0, 1.0 - magic - (1.0 - magic) * MissileShare);
            }
        }

        /// <summary>
        /// Whether melee is one of its attack MODES on the classifier's terms (code review 2026-10-26 F6): its melee mix
        /// reaches ArchetypeClassifier.HybridFloor, or melee is its largest mode. A caster that only swings now and then
        /// (melee mix below the floor) is not a melee creature, so a family of them is not riposte-eligible.
        /// </summary>
        public bool MeleeMode
        {
            get
            {
                var melee = MeleeMix;

                if (!(melee > 0))
                    return false;

                var magic = Math.Clamp(double.IsNaN(CastShare) ? 0.0 : CastShare, 0.0, 1.0);
                var missile = (1.0 - magic) * MissileShare;

                return melee >= ArchetypeClassifier.HybridFloor || (melee >= magic && melee >= missile);
            }
        }

        /// <summary>
        /// The style this member WEIGHTS the draw by when it sets the budget (orchestrator ruling 2026-10-26), on the archetype
        /// classifier's own thresholds rather than "has any spell": a cast share at or above ArchetypeClassifier.CasterShare
        /// weights as a Caster; at or above ArchetypeClassifier.HybridFloor (casting is a mode a player recognises) as a Hybrid
        /// when melee is also a mode (<see cref="MeleeMode"/>), else a Caster; below HybridFloor the casting is incidental and
        /// it weights as the melee or missile creature it is.
        /// </summary>
        public CreatureAbilityFacts WeightStyle()
        {
            var melees = MeleeMode;
            var share = double.IsNaN(CastShare) ? 0.0 : CastShare;
            var casterMode = share >= ArchetypeClassifier.CasterShare;
            var castMode = Casts && share >= ArchetypeClassifier.HybridFloor;

            return new CreatureAbilityFacts
            {
                Missile = MissilePerHit > 0,
                Caster = castMode && (casterMode || !melees),
                Hybrid = castMode && !casterMode && melees,
                Melee = !castMode && melees,
            };
        }

        /// <summary>
        /// This member's style for ELIGIBILITY (the planner unions these): Missile when it shoots; with melee read as a
        /// classifier mode (<see cref="MeleeMode"/>, code review F6) and casting as any harmful spell, Hybrid when it melees
        /// and casts, Caster when it only casts, Melee when it only melees.
        /// </summary>
        public CreatureAbilityFacts Style()
        {
            var melees = MeleeMode;

            return new CreatureAbilityFacts
            {
                Missile = MissilePerHit > 0,
                Hybrid = Casts && melees,
                Caster = Casts && !melees,
                Melee = !Casts && melees,
            };
        }
    }
    /// <summary>The run's family ability decision: the budget, the facts, the chosen abilities and the members it was read from.</summary>
    public sealed class FamilyAbilityPlan
    {
        public static readonly FamilyAbilityPlan None = new FamilyAbilityPlan(null, 0, null, CreatureAbilityResult.Empty, new FamilyAbilityMember[0]);

        public FamilyAbilityPlan(string familyKey, int budget, CreatureAbilityFacts facts, CreatureAbilityResult result, IReadOnlyList<FamilyAbilityMember> members,
            FamilyAbilityMember budgetSetter = null)
        {
            BudgetSetterMember = budgetSetter;
            FamilyKey = familyKey;
            Budget = budget;
            Facts = facts;
            Result = result ?? CreatureAbilityResult.Empty;
            Members = members ?? new FamilyAbilityMember[0];
        }

        public string FamilyKey { get; }

        /// <summary>The MAX predicted withheld group rating over the family's drawn members (trash, elites and the boss).</summary>
        public int Budget { get; }

        public CreatureAbilityFacts Facts { get; }

        /// <summary>What every member of the family is overlaid with (<see cref="CreatureAbilityResult.Empty"/> = nothing).</summary>
        public CreatureAbilityResult Result { get; }

        public IReadOnlyList<FamilyAbilityMember> Members { get; }

        /// <summary>Members whose gear is unknown (<see cref="FamilyAbilityMember.GearUnknown"/>): their prediction, and so their share of the budget, is a floor.</summary>
        public int GearUnknownCount => Members.Count(m => m.GearUnknown);

        /// <summary>The member that set <see cref="Budget"/> (<see cref="FamilyAbilityPlanner.BudgetSetter"/>); its style weights the draw. Null with no members.</summary>
        public FamilyAbilityMember BudgetSetterMember { get; }
    }

    /// <summary>
    /// The family ability budget and selection (DAMAGE-CEILING-PLAN.md step 6). A plan draws ONE family (plan.FamilyId), and the
    /// boss is fielded as one of its members. For every distinct drawn member the planner predicts the per-hit the damage
    /// ceiling will measure at spawn (<see cref="PredictEntry"/>: the reach-up stamp, the archetype or band uplift, the boss
    /// normalization) and the DamageRating R it will carry before the group bonus (<see cref="PredictOwnRating"/>), compresses
    /// it against the plan's median, cap and top, and reads the group rating the clamp withholds
    /// (<see cref="DamageCeiling.WithheldRating"/>). The family budget is the MAX over members and axes, so the family's
    /// hardest hitter sets what the whole family buys. Solo plans (G = 0) withhold nothing and buy nothing.
    ///
    /// STYLE (orchestrator ruling 2026-10-26). ELIGIBILITY reads the UNION of the members' styles (a caster-only kind needs
    /// some member that casts, riposte some member that melees), but the draw WEIGHTING reads the budget setter's own style
    /// (<see cref="BudgetSetter"/>, carried as <see cref="CreatureAbilityFacts.Weighting"/>), read on the archetype
    /// classifier's thresholds (<see cref="FamilyAbilityMember.WeightStyle"/>): a family whose budget comes from a melee
    /// hitter whose casting is incidental (below ArchetypeClassifier.HybridFloor) leans to riposte and execute even when a
    /// caster in it makes the caster kinds eligible.
    ///
    /// The selection is <see cref="ThreadCreatureAbilities.Select"/>, seeded from the run seed and the family key: no draw from
    /// the plan's rng, no plan note, no fingerprint field. Every member of the family gets the same spec.
    ///
    /// Not modelled (the same as the census): the integer rounding of the writes, the Endurance guard on the attribute raise.
    /// </summary>
    public static class FamilyAbilityPlanner
    {
        /// <summary>
        /// The per-hit sources of one plan entry after every build-time damage writer that runs before the ceiling, in
        /// PrepareForPlacement's order: the reach-up stamp (non-boss, plan.StatCurve set), the archetype uplift (an uplifted
        /// non-boss entry the plan resolved an archetype for) or else the band uplift, then the boss normalization (a boss
        /// with plan.BossNormalize and a non-empty band standard: every damaging source SET to the band's MaxBodyDamage x the
        /// offense ratio, attributes SET to the band medians x their ratio).
        /// <paramref name="upliftAttributes"/> is dynamic_dungeons_uplift_attributes.
        /// </summary>
        public static CreaturePerHit PredictEntry(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, CreaturePerHit raw, bool upliftAttributes)
        {
            raw ??= CreaturePerHit.Empty;

            if (plan == null || entry == null)
                return raw;

            var isBoss = entry.Role == DungeonRole.Boss;
            var predicted = raw;

            var stampCurve = ThreadDungeonSpawner.StampCurveFor(entry, plan);

            if (stampCurve != null)
                predicted = predicted.ProjectedTo(entry.StampLevel, stampCurve.Ratio);

            var info = ThreadDungeonSpawner.ArchetypeInfoFor(entry, plan);

            if (info != null)
                predicted = predicted.UpliftedByArchetype(entry.UpliftLevel, info, ThreadDungeonSpawner.StampedDamageRating(plan, false));
            else if (entry.UpliftLevel > 0)
                predicted = predicted.UpliftedByBand(entry.UpliftLevel, plan.BandStandard,
                    ThreadDungeonSpawner.UpliftsAttributes(entry.UpliftLevel, entry.Role, plan.BandStandard, upliftAttributes));

            if (isBoss && plan.BossNormalize && plan.BandStandard != null && !plan.BandStandard.IsEmpty)
                predicted = NormalizedBoss(predicted, plan);

            return predicted;
        }

        /// <summary>
        /// The boss normalization as the per-hit read sees it (DungeonCreatureNormalizer.NormalizeBossAttributes,
        /// SetBossBodyParts and NormalizeBossWeapons): a damaging body source's best part and every weapon's effective
        /// maximum land on the band's MaxBodyDamage x BossOffenseBandRatio; the attribute is SET to its band target. An axis
        /// whose target is 0 (no band damage, a non-positive ratio, no median) is left as it was.
        /// </summary>
        internal static CreaturePerHit NormalizedBoss(CreaturePerHit perHit, DungeonSpawnPlan plan)
        {
            var standard = plan.BandStandard;
            var off = plan.BossOffenseBandRatio;
            var damageTarget = standard.MaxBodyDamage > 0 && off > 0 && !double.IsNaN(off) && !double.IsInfinity(off)
                ? Math.Clamp(Math.Round(standard.MaxBodyDamage * off), 0.0, uint.MaxValue)
                : 0.0;

            PerHitSource Set(PerHitSource s)
            {
                if (s.IsEmpty)
                    return s;

                var attribute = DungeonCombatNormalizer.AttributeTarget(standard, s.Attribute, plan.BossOffenseBandRatio, plan.BossDefenseBandRatio);

                return s.With(damageTarget > 0 ? damageTarget : s.BaseDamage, attribute > 0 ? attribute : s.AttributeValue);
            }

            return new CreaturePerHit(perHit.Level, Set(perHit.Melee), Set(perHit.Missile), perHit.UpliftInputs, perHit.GearUnknown, perHit.ByArchetype);
        }

        /// <summary>
        /// R, the DamageRating a placed creature carries before the group bonus (ApplyDamageCeiling's ownRating): a normalized
        /// boss has its authored ratings zeroed, an archetype-uplifted entry its DamageRating floored
        /// (<see cref="ArchetypeUplift.DamageRatingFloor"/>, written as the larger of it and the authored rating), then the
        /// gem adds plan.BossDamageRating or plan.DamageRating.
        /// </summary>
        public static int PredictOwnRating(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, CreaturePerHit raw)
        {
            if (plan == null || entry == null)
                return 0;

            var authored = raw?.UpliftInputs?.DamageRating ?? 0;

            if (entry.Role == DungeonRole.Boss)
                return (int)Math.Clamp((long)(plan.BossNormalize ? 0 : authored) + plan.BossDamageRating, int.MinValue, int.MaxValue);

            var info = ThreadDungeonSpawner.ArchetypeInfoFor(entry, plan);
            var own = authored;

            if (info != null)
            {
                var floor = ArchetypeUplift.DamageRatingFloor(info, authored, raw?.UpliftInputs?.SpellMeanAt(info.Run.Core.SpellTier) ?? 0.0);
                own = Math.Max(authored, floor.DrTo);
            }

            return (int)Math.Clamp((long)own + plan.DamageRating, int.MinValue, int.MaxValue);
        }

        /// <summary>
        /// The run's family ability plan. <see cref="FamilyAbilityPlan.None"/> for a plan with no family; a plan with no
        /// median or no group bonus reads its members (for the census) but budgets 0 and selects nothing.
        /// </summary>
        /// <param name="perHitOf">The raw per-hit read (the spawner's LivePerHitOf, memoised).</param>
        /// <param name="traitsOf">The member's casting flag and authored effect records (null reads every member as <see cref="AbilityMemberTraits.Empty"/>).</param>
        /// <param name="upliftAttributes">dynamic_dungeons_uplift_attributes.</param>
        /// <param name="settings">CreatureAbilitySettings.Read() live; folds in monster_effects_enabled.</param>
        /// <param name="runSeed">The run's seed (the gem seed).</param>
        public static FamilyAbilityPlan Resolve(DungeonSpawnPlan plan, Func<uint, CreaturePerHit> perHitOf, Func<uint, AbilityMemberTraits> traitsOf,
            bool upliftAttributes, CreatureAbilitySettings settings, long runSeed)
        {
            if (plan == null || perHitOf == null || plan.Entries == null || plan.Entries.Count == 0)
                return FamilyAbilityPlan.None;

            var familyKey = plan.FamilyId ?? "none";
            var median = plan.PerHitMedian;
            var groupBonus = Math.Max(0, plan.GroupDamageRatingBonus);
            var members = new List<FamilyAbilityMember>();

            var facts = new CreatureAbilityFacts { BandSpellTier = plan.BandStandard?.SpellTier ?? 0 };
            var kinds = new SortedSet<string>(StringComparer.Ordinal);

            // Distinct by everything the prediction reads, in first-drawn order (the boss entry is last in Entries).
            var seen = new HashSet<(uint, bool, int, int)>();

            foreach (var entry in plan.Entries)
            {
                if (entry == null || !seen.Add((entry.Wcid, entry.Role == DungeonRole.Boss, entry.UpliftLevel, entry.StampLevel)))
                    continue;

                var raw = perHitOf(entry.Wcid) ?? CreaturePerHit.Empty;
                var predicted = PredictEntry(plan, entry, raw, upliftAttributes);
                var own = PredictOwnRating(plan, entry, raw);

                int Withheld(double perHit)
                    => median > 0 ? DamageCeiling.WithheldRating(DamageCeiling.TargetPerHit(perHit, median, plan.DamageCeilingCap, plan.DamageCeilingTop),
                        median, plan.DamageCeilingCap, own, groupBonus) : 0;

                // Attack style from the PREDICTED sources (what it fights with once placed) and the caller's casting flag.
                var traits = traitsOf?.Invoke(entry.Wcid) ?? AbilityMemberTraits.Empty;
                var member = new FamilyAbilityMember(entry.Wcid, entry.Role, own, predicted.MeleePerHit, predicted.MissilePerHit,
                    Withheld(predicted.MeleePerHit), Withheld(predicted.MissilePerHit), traits.Casts, traits.CastShare, traits.MissileShare, predicted.GearUnknown);

                members.Add(member);

                // ELIGIBILITY: the union of every member's style.
                var style = member.Style();

                facts.Missile |= style.Missile;
                facts.Hybrid |= style.Hybrid;
                facts.Caster |= style.Caster;
                facts.Melee |= style.Melee;

                foreach (var kind in traits.AuthoredKinds)
                    kinds.Add(kind);

                facts.AuthoredRecordCount = Math.Max(facts.AuthoredRecordCount, traits.AuthoredRecordCount);
            }

            facts.AuthoredEffectKinds = kinds.ToList();

            // WEIGHTING (orchestrator ruling 2026-10-26): the budget setter's OWN style, so the abilities lean toward how the
            // member that bought them actually fights; the union above still decides what the family may draw at all.
            var setter = BudgetSetter(members);
            var budget = setter?.Withheld ?? 0;

            if (setter != null)
                facts.Weighting = setter.WeightStyle();

            var result = ThreadCreatureAbilities.Select(settings, runSeed, familyKey, budget, facts);

            return new FamilyAbilityPlan(familyKey, budget, facts, result, members, setter);
        }

        /// <summary>
        /// The member whose withheld rating is the family budget: the largest <see cref="FamilyAbilityMember.Withheld"/>; on a
        /// tie, the one whose withheld rating came from the larger hit (<see cref="FamilyAbilityMember.WithheldHit"/>); then
        /// the first drawn. Null for no members.
        /// </summary>
        public static FamilyAbilityMember BudgetSetter(IReadOnlyList<FamilyAbilityMember> members)
        {
            FamilyAbilityMember best = null;

            foreach (var m in members ?? new FamilyAbilityMember[0])
            {
                if (best == null || m.Withheld > best.Withheld || (m.Withheld == best.Withheld && m.WithheldHit > best.WithheldHit))
                    best = m;
            }

            return best;
        }
    }
}
