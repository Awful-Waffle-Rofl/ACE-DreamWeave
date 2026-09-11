using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure CAP arithmetic over a set of class ability definitions: how many class ability points it costs to
    /// max every entry a class owns, per tier and in total. This is the number the design tables are written
    /// in (DESIGN.md and the per-class docs under Docs/ClassAbilities all quote a "T1 / T2 / T3 / total" CAP
    /// row), and it is also what decides whether maxing one tier can pay for the next tier's
    /// "spent in class" gate - see the <c>class_ability_tier3_spent_required</c> tunable read by
    /// <see cref="ACE.Server.WorldObjects.Player.MeetsClassAbilityTierUnlock"/>.
    ///
    /// Kept as a pure static over an arbitrary sequence, with no live Player and no registry lookup of its
    /// own, so a test can assert a class's published totals against the definitions actually registered.
    /// Mirrors the testability pattern of
    /// <see cref="ClassAbilityTokenCatalog.Evaluate"/>.
    ///
    /// It sums <see cref="ClassAbilityDefinition.CostPerRank"/> in full, i.e. the cost of MAXING each entry,
    /// and it counts an entry regardless of <see cref="ClassAbilityDefinition.Implemented"/>: a class's
    /// published CAP budget is a property of its design table, not of which mechanics have shipped yet.
    /// </summary>
    public static class ClassAbilityCostSummary
    {
        /// <summary>
        /// Total CAP to take every entry of <paramref name="abilityClass"/> at exactly <paramref name="tier"/>
        /// to its max rank. 0 if the class owns no entry at that tier.
        /// </summary>
        public static int MaxCostForClassTier(IEnumerable<ClassAbilityDefinition> definitions, ClassAbilityClass abilityClass, int tier)
        {
            var total = 0;

            foreach (var def in definitions)
            {
                if (def == null || def.AbilityClass != abilityClass || def.Tier != tier)
                    continue;

                total += def.CumulativeCost(def.MaxRank);
            }

            return total;
        }

        /// <summary>
        /// Total CAP to take every entry of <paramref name="abilityClass"/>, at every tier, to its max rank.
        /// </summary>
        public static int MaxCostForClass(IEnumerable<ClassAbilityDefinition> definitions, ClassAbilityClass abilityClass)
        {
            var total = 0;

            foreach (var def in definitions)
            {
                if (def == null || def.AbilityClass != abilityClass)
                    continue;

                total += def.CumulativeCost(def.MaxRank);
            }

            return total;
        }

        /// <summary>
        /// How many entries <paramref name="abilityClass"/> owns at <paramref name="tier"/> - the "4 / 4 / 3"
        /// shape the class tables are required to follow.
        /// </summary>
        public static int EntryCountForClassTier(IEnumerable<ClassAbilityDefinition> definitions, ClassAbilityClass abilityClass, int tier)
        {
            var count = 0;

            foreach (var def in definitions)
            {
                if (def == null || def.AbilityClass != abilityClass || def.Tier != tier)
                    continue;

                count++;
            }

            return count;
        }
    }
}
