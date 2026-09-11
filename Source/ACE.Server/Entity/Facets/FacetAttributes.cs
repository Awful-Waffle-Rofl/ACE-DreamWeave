using System.Collections.Generic;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// Reconciles a stored per-facet attribute arrangement against the live character before it is
    /// applied.
    ///
    /// WHAT IS PER-FACET, AND WHY ONLY THIS. A facet stores the six primary attributes' InitLevel -
    /// CreatureAttribute.StartingValue, the redistributable half, and the only half
    /// AttributeTransferDevice moves. The XP-bought half (CPSpent / LevelFromCP, read as
    /// ExperienceSpent / Ranks) is NOT stored and stays global, along with the vital records in
    /// biota_properties_attribute_2nd, augmentations, enlightenment, the spellbook and level.
    ///
    /// That boundary is what keeps this class out of FacetPools entirely. InitLevel is not bought with
    /// experience, so nothing here releases or commits a spendable pool, and there is no delta
    /// arithmetic to get wrong. This class does not reference FacetPools and must never need to.
    ///
    /// THE INVARIANT. Let A(F) be the sum of the six stored values in facet F, and L the same sum on the
    /// live character at this instant. A switch conserves exactly when A(F) == L at the moment F is
    /// applied. Capture writes A(outgoing) := L by copying the live values, so capture can never break
    /// it; only the APPLY side can, and only because the live sum can have moved since the row was
    /// written. Unlike the skill-credit invariant this is directly computable at runtime - two sums, no
    /// dat read and no cost function - so it is checked here rather than asserted in a comment.
    ///
    /// EVERY WRITER OF A PLAYER'S ATTRIBUTE InitLevel, and its effect on L:
    ///   - AttributeTransferDevice           - moves up to 10 between two attributes. STRICTLY CONSERVING.
    ///   - AugmentationDevice (innate aug)   - ADDS 5, or min(5, 100 - value) under the
    ///                                         attribute_augmentation_safety_cap tunable. RAISES L.
    ///   - DeveloperFixCommands verify-attributes fix - moves 5 from an over-100 attribute to the lowest
    ///                                         eligible one. CONSERVING.
    ///   - AdminCommands @modifyattr         - arbitrary signed delta, clamped per attribute to [1, 9999].
    ///                                         Can raise OR LOWER L.
    ///   - PlayerFactory                     - character creation only, before any facet exists.
    ///   - Player_Mule                       - unreachable: a mule is refused a facet switch outright.
    ///   - Player_SurvivalChallenge          - creatures only, never a Player.
    ///
    /// So exactly two things can break the invariant, and each gets a fixed handling rule:
    ///
    ///   - L &gt; sum(stored): the character gained innate points since the row was written. RECONCILE UP,
    ///     never refuse. Refusing would strand every stored facet the moment a player buys an innate
    ///     augmentation - which is an ordinary purchase, not an error - and no ordinary player action can
    ///     lower L, so this is the common drift and it is always benign. The surplus is deposited
    ///     lowest-first, ties broken by PropertyAttribute enum order, capped at 100 per attribute;
    ///     anything still left over after that walk goes ENTIRELY onto the first attribute in that same
    ///     order rather than being clamped away. Lowest-first mirrors what verify-attributes fix already
    ///     chooses when it has 5 points to place. Not clamping is deliberate: values above 100 are
    ///     already reachable when attribute_augmentation_safety_cap is false, and a hard clamp would
    ///     silently DESTROY points, which is precisely the failure this class exists to prevent.
    ///
    ///   - L &lt; sum(stored): only reachable through an admin subtraction (@modifyattr with a negative
    ///     delta). REFUSE, and let the caller say so. There is no safe automatic answer: any arrangement
    ///     that sums to L is one the player did not choose, and silently deleting the difference is the
    ///     exact failure mode this design exists to prevent.
    ///
    /// Pure and static - no Player, no DatManager, no database - the same testability seam FacetPools
    /// takes, and for the same reason: ACE.Server.Tests cannot construct a live Player.
    /// </summary>
    public static class FacetAttributes
    {
        /// <summary>
        /// The six primary attributes, in PropertyAttribute enum order. Note that the enum orders
        /// Quickness (3) BEFORE Coordination (4), matching the client; this array is the tiebreak order
        /// the surplus walk uses, so it must stay in enum order rather than display order.
        /// </summary>
        public static readonly PropertyAttribute[] PrimaryAttributes =
        {
            PropertyAttribute.Strength,
            PropertyAttribute.Endurance,
            PropertyAttribute.Quickness,
            PropertyAttribute.Coordination,
            PropertyAttribute.Focus,
            PropertyAttribute.Self,
        };

        /// <summary>The per-attribute ceiling the surplus walk fills up to before moving to the next.</summary>
        public const uint AttributeCeiling = 100;

        /// <summary>
        /// The sum of the SIX PRIMARY attributes in an arrangement. Only the six, never every key
        /// present: DeveloperFixCommands' verify-attributes exists specifically because a biota's
        /// PropertiesAttribute collection can carry keys outside Strength..Self, and folding one of
        /// those into the sum would make the live and stored totals disagree for a reason that has
        /// nothing to do with the player's redistribution.
        ///
        /// Returns 0 for a null arrangement, and treats an absent attribute as 0 - callers that need
        /// "all six present" enforce that themselves (FacetSnapshot.TryDeserializeAttributes does).
        /// </summary>
        public static uint Sum(IReadOnlyDictionary<PropertyAttribute, uint> arrangement)
        {
            if (arrangement == null)
                return 0;

            ulong total = 0;

            foreach (var attribute in PrimaryAttributes)
            {
                if (arrangement.TryGetValue(attribute, out var value))
                    total += value;
            }

            // Cannot overflow in practice: @modifyattr clamps each attribute to [1, 9999] and it is the
            // only writer that can set a large value, so six of them cap at 59,994. Summed in ulong
            // anyway so the bound is a property of the data rather than of this loop.
            return (uint)total;
        }

        /// <summary>
        /// The arrangement to apply, given what the facet stored and what the live character's six
        /// primary attributes currently sum to.
        ///
        /// Returns NULL when the stored arrangement cannot be applied conservingly, which happens in
        /// exactly two ways, distinguished by <paramref name="shortfall"/>:
        ///   - shortfall &gt; 0: the live sum is SHORT of the stored sum by that much (the admin-subtraction
        ///     case). The caller must refuse the switch before mutating anything.
        ///   - shortfall == 0: <paramref name="stored"/> was null or did not name all six primary
        ///     attributes. Unreachable from the switch path, which only calls this after
        ///     FacetSnapshot.TryDeserializeAttributes has already returned a complete six-attribute
        ///     arrangement; kept as a guard rather than an assertion so a future caller cannot silently
        ///     apply a partial arrangement.
        ///
        /// On a non-null return the result ALWAYS satisfies Sum(result) == liveSum. That is the whole
        /// point of the class, and it holds including when the surplus runs past every ceiling.
        /// </summary>
        /// <param name="stored">The facet's stored arrangement. Must name all six primary attributes.</param>
        /// <param name="liveSum">The sum of the live character's six primary InitLevel values right now.</param>
        /// <param name="surplusApplied">
        /// How many points the live character had beyond the stored arrangement and which were therefore
        /// deposited into it. Zero when the two sums already agreed. The caller can see WHERE they landed
        /// by diffing the result against <paramref name="stored"/>.
        /// </param>
        /// <param name="shortfall">
        /// How many points the live character is SHORT of the stored arrangement. Non-zero only on the
        /// refuse path.
        /// </param>
        public static Dictionary<PropertyAttribute, uint> Reconcile(
            IReadOnlyDictionary<PropertyAttribute, uint> stored,
            uint liveSum,
            out uint surplusApplied,
            out uint shortfall)
        {
            surplusApplied = 0;
            shortfall = 0;

            if (stored == null)
                return null;

            var result = new Dictionary<PropertyAttribute, uint>();

            foreach (var attribute in PrimaryAttributes)
            {
                if (!stored.TryGetValue(attribute, out var value))
                    return null;   // partial arrangement - see the remarks; never applied

                result[attribute] = value;
            }

            var storedSum = Sum(result);

            if (liveSum < storedSum)
            {
                shortfall = storedSum - liveSum;
                return null;
            }

            var surplus = liveSum - storedSum;

            if (surplus == 0)
                return result;      // verbatim - the ordinary case

            // The deposit ORDER is computed once, from the STORED values, and then held fixed: lowest
            // value first, ties broken by PropertyAttribute enum order. Recomputing "lowest" after each
            // deposit would spread the surplus evenly across the arrangement instead of filling the
            // lowest attributes first, which is not what verify-attributes fix does and not what was
            // approved.
            var order = new List<PropertyAttribute>(PrimaryAttributes);

            order.Sort((a, b) =>
            {
                var byValue = result[a].CompareTo(result[b]);

                return byValue != 0 ? byValue : ((ushort)a).CompareTo((ushort)b);
            });

            var remaining = surplus;

            foreach (var attribute in order)
            {
                if (remaining == 0)
                    break;

                var value = result[attribute];

                if (value >= AttributeCeiling)
                    continue;       // already at or above the ceiling - it takes nothing

                var room = AttributeCeiling - value;
                var take = room < remaining ? room : remaining;

                result[attribute] = value + take;
                remaining -= take;
            }

            // Everything at the ceiling and points still in hand. They go entirely onto the first
            // attribute in the deposit order rather than being clamped away: a clamp here would destroy
            // real points, and an above-100 innate value is already a state the server produces on its
            // own whenever attribute_augmentation_safety_cap is false.
            if (remaining > 0)
                result[order[0]] += remaining;

            surplusApplied = surplus;

            return result;
        }
    }
}
