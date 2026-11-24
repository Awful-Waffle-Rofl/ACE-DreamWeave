using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The kind of an XP-bought trait. Skills carry their advancement class separately (TraitState.Sac), so one
    /// Skill kind covers trained and specialized. ACE.Server cannot reference ACE.Content.Tools, where
    /// TrialTraitKind lives, so the lift keeps its own three-member kind.
    /// </summary>
    public enum TraitKind
    {
        Attribute,
        Vital,
        Skill,
    }

    /// <summary>One trait as the anchor holds it: kind, id (PropertyAttribute, PropertyAttribute2nd or Skill), SAC (skills only) and XP spent.</summary>
    public sealed record TraitState(TraitKind Kind, int Id, int Sac, uint Spent);

    /// <summary>
    /// A trait the lift changed, as TrialLiftApply writes it: the new SAC (skills), XP spent and ranks. NewlyTrained
    /// marks a skill the credit spend trained, which also gets InitLevel 0 as TrainSkill writes it.
    /// </summary>
    public sealed record TraitChange(TraitKind Kind, int Id, int Sac, uint Spent, int Ranks, bool NewlyTrained);

    /// <summary>
    /// The anchor's persisted level-up state. Enlightenment feeds the retirement-floor check (step 9);
    /// AugmentedSkills are the skills an augmentation the anchor holds would specialize on training (step 5).
    /// </summary>
    public sealed record TrialLiftInput(
        int Level,
        long TotalXp,
        long UnspentXp,
        int UnspentCredits,
        int TotalCredits,
        int MilestoneCapGranted,
        int CapBoughtWithXp,
        IReadOnlyList<TraitState> Traits,
        int TargetLevel,
        int Enlightenment = 0,
        IReadOnlyCollection<Skill> AugmentedSkills = null);

    /// <summary>
    /// The calibrated cohort figures the spend reads: skill credits held and CAP bought with XP (p50s, null spends
    /// nothing on that axis), and the order in which a cohort trains skills (empty trains nothing).
    /// </summary>
    public sealed record CohortTargets(int? UnspentCredits, int? CapBoughtWithXp, IReadOnlyList<Skill> TrainOrder);

    /// <summary>The class_ability_xp_* tunables the XP lane prices with (Player.XpCostForClassAbilityPoints, Player.ClassAbilityXpMinLevel).</summary>
    public sealed record XpLaneTunables(long BaseCost, double Ratio1, double Ratio2, double Ratio3, int Breakpoint1, int Breakpoint2, int MinLevel)
    {
        /// <summary>The PropertyManager keys, in record order. Longs: base cost, breakpoints, min level; doubles: the three ratios.</summary>
        public const string BaseCostKey = "class_ability_xp_base_cost";
        public const string Ratio1Key = "class_ability_xp_ratio_1";
        public const string Ratio2Key = "class_ability_xp_ratio_2";
        public const string Ratio3Key = "class_ability_xp_ratio_3";
        public const string Breakpoint1Key = "class_ability_xp_breakpoint_1";
        public const string Breakpoint2Key = "class_ability_xp_breakpoint_2";
        public const string MinLevelKey = "class_ability_xp_min_level";
    }

    /// <summary>What a lift produced. Counters are absolute values; CapAdded is what CAP earned and CAP available each grow by.</summary>
    public sealed record TrialLiftResult(
        int Level,
        long TotalXp,
        long UnspentXp,
        int UnspentCredits,
        int TotalCredits,
        int MilestoneCapGranted,
        int CapBoughtWithXp,
        int CapAdded,
        long XpGranted,
        int CreditsGranted,
        int MilestoneCapAdded,
        int CapBoughtThisLift,
        long CapXpSpent,
        IReadOnlyList<TraitChange> Changes,
        IReadOnlyList<Skill> TrainedSkills,
        IReadOnlyList<TraitState> Unmaxed)
    {
        public bool Lifted => Changes.Count > 0 || XpGranted != 0 || CreditsGranted != 0 || CapAdded != 0;
    }

    /// <summary>
    /// The offline level-up of a parked master (Balance Trials Plan B Task 7). Pure: the charts and tunables are passed
    /// in, and every rule is a call to the live function the server uses - the loop criterion of CheckForLevelup over
    /// EnlightenmentXpCurve totals, EnlightenmentXpCurve.GetSkillCreditsForLevel, ClassAbilityMilestones.EntitledCount,
    /// ClassAbilityLumCurve.CostForRange (behind Player.XpCostForClassAbilityPoints) and
    /// EnlightenmentRetirement.EquivalentLevel. Nothing here touches EF, the dats or PropertyManager.
    /// </summary>
    public static class TrialLevelLift
    {
        /// <summary>The level CheckForLevelup's loop settles on for a total: the largest l with totals[l] &lt;= totalXp.</summary>
        public static int LoopLevel(IReadOnlyList<ulong> totals, long totalXp)
        {
            if (totalXp < 0 || totals.Count == 0)
                return 0;

            var level = 0;
            while (level + 1 < totals.Count && (ulong)totalXp >= totals[level + 1])
                level++;
            return level;
        }

        /// <summary>Skill credits granted for reaching each level in the half-open range (fromLevel, toLevel], as CheckForLevelup adds them.</summary>
        public static int CreditsBetween(int fromLevel, int toLevel, IReadOnlyList<uint> creditList)
        {
            long credits = 0;
            for (var l = fromLevel + 1; l <= toLevel; l++)
                credits += EnlightenmentXpCurve.GetSkillCreditsForLevel(l, creditList);
            return checked((int)credits);
        }

        /// <summary>Milestone CAP a lift owes: EntitledCount at the target less what the anchor's level or counter already covers, never negative.</summary>
        public static int MilestoneCapOwed(int fromLevel, int toLevel, int milestoneGranted)
        {
            var owed = ClassAbilityMilestones.EntitledCount(toLevel) - Math.Max(ClassAbilityMilestones.EntitledCount(fromLevel), milestoneGranted);
            return Math.Max(0, owed);
        }

        /// <summary>The XP price of buying count points after alreadyPurchased, at the given tunables (Player.XpCostForClassAbilityPoints's math).</summary>
        public static long XpCost(int alreadyPurchased, int count, XpLaneTunables t) =>
            ClassAbilityLumCurve.CostForRange(alreadyPurchased, count, t.BaseCost, t.Ratio1, t.Ratio2, t.Ratio3, t.Breakpoint1, t.Breakpoint2);

        /// <summary>The rank chart a trait advances on: attributes, vitals, trained or specialized skills. Null for an untrained skill.</summary>
        public static IReadOnlyList<uint> ChartFor(XpCharts c, TraitKind kind, int sac) => kind switch
        {
            TraitKind.Attribute => c.AttributeXp,
            TraitKind.Vital => c.VitalXp,
            TraitKind.Skill when sac == (int)SkillAdvancementClass.Specialized => c.SpecializedSkillXp,
            TraitKind.Skill when sac == (int)SkillAdvancementClass.Trained => c.TrainedSkillXp,
            _ => null,
        };

        /// <summary>
        /// The augmentations that specialize a skill on training, as Player.IsSkillSpecializedViaAugmentation reads them.
        /// Its key set is pinned against Player.AugSpecSkills by a test.
        /// </summary>
        public static readonly IReadOnlyDictionary<Skill, PropertyInt> SkillSpecializingAugmentations = new Dictionary<Skill, PropertyInt>
        {
            [Skill.ArmorTinkering] = PropertyInt.AugmentationSpecializeArmorTinkering,
            [Skill.ItemTinkering] = PropertyInt.AugmentationSpecializeItemTinkering,
            [Skill.MagicItemTinkering] = PropertyInt.AugmentationSpecializeMagicItemTinkering,
            [Skill.WeaponTinkering] = PropertyInt.AugmentationSpecializeWeaponTinkering,
            [Skill.Salvaging] = PropertyInt.AugmentationSpecializeSalvaging,
        };

        /// <summary>
        /// Lifts <paramref name="i"/> to i.TargetLevel and spends what the lift creates. Returns null with a refusal for an
        /// inconsistent or illegal input; the refusal names levels and amounts only.
        /// </summary>
        public static TrialLiftResult Lift(TrialLiftInput i, XpCharts c, XpLaneTunables t, CohortTargets k, out string refusal)
        {
            refusal = null;
            var totals = c.LevelTotals;
            var T = i.TargetLevel;

            // 1. Preconditions.
            if (i.TotalXp < 0 || i.UnspentXp < 0)
            {
                refusal = $"negative experience (total {i.TotalXp}, unspent {i.UnspentXp})";
                return null;
            }

            var loop = LoopLevel(totals, i.TotalXp);
            if (loop != i.Level)
            {
                refusal = $"stored level {i.Level} is not the level the live loop gives for the stored total experience ({loop})";
                return null;
            }

            if (i.Level < EnlightenmentXpCurve.BaseMaxLevel)
            {
                refusal = $"anchor level {i.Level} is below {EnlightenmentXpCurve.BaseMaxLevel}, where the credit chart and the CAP milestones end";
                return null;
            }

            if (T < i.Level)
            {
                refusal = $"target level {T} is below the anchor level {i.Level}; a lift never down-levels";
                return null;
            }

            if (T > totals.Count - 1)
            {
                refusal = $"target level {T} is past the chart's last level {totals.Count - 1}";
                return null;
            }

            if (T == i.Level)
                return Unchanged(i);

            // 2. Level: keep the progress within the level, clamped to stay below the next level.
            var progress = (ulong)i.TotalXp - totals[i.Level];
            ulong newTotalU;
            if (T + 1 < totals.Count)
                newTotalU = totals[T] + Math.Min(progress, totals[T + 1] - totals[T] - 1);
            else
                newTotalU = totals[T];   // the chart's last level: nothing above it to stay below

            var newTotal = checked((long)newTotalU);
            if (LoopLevel(totals, newTotal) != T)
                throw new InvalidOperationException($"lift arithmetic: the lifted total lands on level {LoopLevel(totals, newTotal)}, not {T}");

            var xpGranted = newTotal - i.TotalXp;
            var unspentXp = checked(i.UnspentXp + xpGranted);

            // 9. The login-time retirement credit must find nothing to do.
            if (i.Enlightenment > 0)
            {
                var floorLevel = EnlightenmentRetirement.EquivalentLevel(i.Enlightenment, totals);
                if (floorLevel > 0 && (ulong)newTotal < totals[floorLevel])
                {
                    refusal = $"the lifted total sits below the enlightenment retirement floor (level {floorLevel} for {i.Enlightenment} enlightenment(s)); the first login would rewrite it";
                    return null;
                }
            }

            // 3. Credits over (anchor level, target].
            var creditsGranted = CreditsBetween(i.Level, T, c.LevelCredits);
            var unspentCredits = i.UnspentCredits + creditsGranted;
            var totalCredits = i.TotalCredits + creditsGranted;

            // 4. Milestone CAP.
            var milestoneOwed = MilestoneCapOwed(i.Level, T, i.MilestoneCapGranted);
            var milestoneGranted = milestoneOwed > 0 ? ClassAbilityMilestones.EntitledCount(T) : i.MilestoneCapGranted;

            // 5. Spend credits: the cohort's order, down to the cohort's credits held.
            var traits = i.Traits.ToList();
            var augmented = new HashSet<Skill>(i.AugmentedSkills ?? Array.Empty<Skill>());
            var trained = new List<Skill>();
            if (k.UnspentCredits is int keep)
            {
                while (unspentCredits > keep)
                {
                    var pick = (k.TrainOrder ?? Array.Empty<Skill>()).Cast<Skill?>().FirstOrDefault(s =>
                        !IsTrained(traits, s.Value)
                        && !augmented.Contains(s.Value)
                        && c.TrainedCost.TryGetValue(s.Value, out var cost) && cost >= 0 && cost <= unspentCredits);
                    if (pick == null)
                        break;

                    var skill = pick.Value;
                    unspentCredits -= c.TrainedCost[skill];
                    traits.RemoveAll(x => x.Kind == TraitKind.Skill && x.Id == (int)skill);
                    traits.Add(new TraitState(TraitKind.Skill, (int)skill, (int)SkillAdvancementClass.Trained, 0));
                    trained.Add(skill);
                }
            }

            // 6. Spend XP on traits: attributes, vitals, then trained/specialized skills, each by id, topped to its chart.
            var changes = new Dictionary<(TraitKind, int), TraitChange>();
            foreach (var skill in trained)
                changes[(TraitKind.Skill, (int)skill)] = new TraitChange(TraitKind.Skill, (int)skill, (int)SkillAdvancementClass.Trained, 0, 0, true);

            var unmaxed = new List<TraitState>();
            foreach (var trait in traits.OrderBy(x => x.Kind).ThenBy(x => x.Id))
            {
                var chart = ChartFor(c, trait.Kind, trait.Sac);
                if (chart == null || chart.Count == 0)
                    continue;

                var last = chart[chart.Count - 1];
                if (trait.Spent >= last)
                    continue;

                var cost = (long)last - trait.Spent;
                if (unspentXp >= cost)
                {
                    unspentXp -= cost;
                    var newlyTrained = trained.Contains((Skill)trait.Id) && trait.Kind == TraitKind.Skill;
                    changes[(trait.Kind, trait.Id)] = new TraitChange(trait.Kind, trait.Id, trait.Sac, last, chart.Count - 1, newlyTrained);
                }
                else
                {
                    unmaxed.Add(trait);
                }
            }

            // 7. Spend XP on CAP: toward the cohort's amount, at the live price, unassigned XP only.
            var capBought = 0;
            long capCost = 0;
            if (T >= t.MinLevel && k.CapBoughtWithXp is int capTarget)
            {
                var want = Math.Max(0, capTarget - i.CapBoughtWithXp);
                for (var n = 1; n <= want; n++)
                {
                    var cost = XpCost(i.CapBoughtWithXp, n, t);
                    if (cost > unspentXp)
                        break;
                    capBought = n;
                    capCost = cost;
                }
                unspentXp -= capCost;
            }

            // 8. Leftover XP stays unspent.
            return new TrialLiftResult(
                Level: T,
                TotalXp: newTotal,
                UnspentXp: unspentXp,
                UnspentCredits: unspentCredits,
                TotalCredits: totalCredits,
                MilestoneCapGranted: milestoneGranted,
                CapBoughtWithXp: i.CapBoughtWithXp + capBought,
                CapAdded: milestoneOwed + capBought,
                XpGranted: xpGranted,
                CreditsGranted: creditsGranted,
                MilestoneCapAdded: milestoneOwed,
                CapBoughtThisLift: capBought,
                CapXpSpent: capCost,
                Changes: changes.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToList(),
                TrainedSkills: trained,
                Unmaxed: unmaxed);
        }

        private static bool IsTrained(List<TraitState> traits, Skill skill) =>
            traits.Any(x => x.Kind == TraitKind.Skill && x.Id == (int)skill && x.Sac >= (int)SkillAdvancementClass.Trained);

        private static TrialLiftResult Unchanged(TrialLiftInput i) => new TrialLiftResult(
            i.Level, i.TotalXp, i.UnspentXp, i.UnspentCredits, i.TotalCredits, i.MilestoneCapGranted, i.CapBoughtWithXp,
            CapAdded: 0, XpGranted: 0, CreditsGranted: 0, MilestoneCapAdded: 0, CapBoughtThisLift: 0, CapXpSpent: 0,
            Changes: Array.Empty<TraitChange>(), TrainedSkills: Array.Empty<Skill>(), Unmaxed: Array.Empty<TraitState>());
    }
}
