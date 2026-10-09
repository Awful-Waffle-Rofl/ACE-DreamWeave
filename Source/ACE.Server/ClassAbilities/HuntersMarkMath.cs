using System;
using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for Hunter's Mark (Archer T1): how the marks several archers have placed on ONE creature
    /// combine into the single multiplier Creature.GetHuntersMarkMod returns.
    ///
    /// Each archer owns exactly one registry entry on the target (HuntersMarkAbility.ApplyMark refreshes
    /// only its own caster's entry), whose StatModValue is 1.0 + that archer's mark percent. This class turns
    /// the list of those per-caster values into one factor.
    ///
    /// <see cref="StackAdditively"/> IS THE SINGLE STACKING DECISION POINT. Owner ruling 2026-09-14: when
    /// several archers mark the same target, the STRONGEST mark applies, so it is false. Flipping it to true
    /// switches every reader to the additive rule (1 + the sum of each mark's excess) with no other edit.
    /// </summary>
    public static class HuntersMarkMath
    {
        /// <summary>
        /// false = strongest mark applies (owner ruling 2026-09-14). true = marks add their excess over 1.0.
        /// </summary>
        public const bool StackAdditively = false;

        /// <summary>
        /// Combines per-caster mark multipliers into one factor under the rule <see cref="StackAdditively"/>
        /// selects. Values at or below 1.0 carry no mark and are ignored; an empty or null list is 1.0.
        /// </summary>
        public static float Combine(IReadOnlyList<float> perCasterMods)
        {
            return StackAdditively ? CombineAdditive(perCasterMods) : CombineStrongest(perCasterMods);
        }

        /// <summary>
        /// The strongest-applies rule: the largest multiplier above 1.0, or 1.0 when there is none.
        /// </summary>
        public static float CombineStrongest(IReadOnlyList<float> perCasterMods)
        {
            var result = 1.0f;

            if (perCasterMods == null)
                return result;

            for (var i = 0; i < perCasterMods.Count; i++)
            {
                if (perCasterMods[i] > result)
                    result = perCasterMods[i];
            }

            return result;
        }

        /// <summary>
        /// The additive rule: 1.0 plus the sum of every multiplier's excess over 1.0 (two +3% marks make
        /// +6%, not 1.03 * 1.03). Values at or below 1.0 contribute nothing.
        /// </summary>
        public static float CombineAdditive(IReadOnlyList<float> perCasterMods)
        {
            var excess = 0.0f;

            if (perCasterMods == null)
                return 1.0f;

            for (var i = 0; i < perCasterMods.Count; i++)
            {
                if (perCasterMods[i] > 1.0f)
                    excess += perCasterMods[i] - 1.0f;
            }

            return 1.0f + Math.Max(0.0f, excess);
        }
    }
}
