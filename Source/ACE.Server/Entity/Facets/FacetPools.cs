using System;
using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// What one skill costs in skill credits. Both values are static per-skill numbers from
    /// portal.dat's SkillTable; the server already treats them as authoritative and verifies a
    /// client's claimed cost against them.
    /// </summary>
    public readonly struct SkillCreditCost
    {
        public SkillCreditCost(int trained, int specialized)
        {
            Trained = trained;
            Specialized = specialized;
        }

        /// <summary>SkillBase.TrainedCost - charged once for any Trained or Specialized skill.</summary>
        public int Trained { get; }

        /// <summary>SkillBase.UpgradeCostFromTrainedToSpecialized - charged ON TOP of Trained.</summary>
        public int Specialized { get; }
    }

    /// <summary>
    /// Derivation of the three spendable pools for a facet.
    ///
    /// Nothing here is stored in character_facet, deliberately. Storing a pool per slot would delete
    /// progression: XP earned while standing on facet 2 would vanish the moment the player switched
    /// back to facet 1's stale snapshot. Carrying a delta instead means every facet sees every point
    /// of progression the character has ever earned.
    ///
    /// ALL THREE POOLS USE THE DELTA FORM - available = current + releasedByOutgoing - requiredByIncoming,
    /// with the switch REFUSED when that goes negative. None of them is re-derived from a lifetime ledger,
    /// and that is the single most important property of this class. The LEDGER form
    /// (available = lifetimeEarned - spent(currentBuild)) is only correct when the build is the ONLY sink
    /// for that resource AND the cost function exactly reproduces what the engine charged. Both
    /// assumptions turned out to be false in ordinary play, in two different ways:
    ///
    ///   - Class ability points have a sink OUTSIDE the build. They are a vendor alternate currency
    ///     (Player_Bank.DebitBankedAlternateCurrency) and back prepaid token vouchers
    ///     (Player_ClassAbilityTokens), both of which debit AvailableClassAbilityPoints without touching
    ///     TotalClassAbilityPointsEarned. A ledger recompute therefore REFUNDED every point ever spent at
    ///     a vendor, on every switch, for free.
    ///
    ///   - Skill credits are mispriced by any generic cost lookup. The engine specializes an
    ///     augmentation-specialized skill for ZERO credits (Player_Skills.SpecializeSkill(skill, 0, false),
    ///     and AugmentationDevice.DoAugmentation sets SAC directly while charging experience only) while
    ///     the dat's generic UpgradeCostFromTrainedToSpecialized for those five skills is 999, and
    ///     ArcaneLore is trained for free at character creation (PlayerFactory's heritage NormalCost 0)
    ///     against a generic TrainedCost of 4. A ledger recompute therefore charged thousands of phantom
    ///     credits and clamped the pool to zero.
    ///
    /// The delta form survives BOTH of those, for two different reasons, and it is worth being precise
    /// about which reason applies to which pool:
    ///
    ///   - For class ability points, the vendor spend is simply never re-derived at all: it is already
    ///     baked into `current`, and only the build-to-build difference is applied on top.
    ///
    ///   - For skill credits, the delta form is NOT by itself enough, and believing it was is how a
    ///     credit-duplication hole got in. State the property in its strong form and the weak argument
    ///     stops being tempting:
    ///
    ///       C(B) == T(B) for every build B
    ///
    ///     where C is what SkillCreditsSpent computes and T is what the engine would charge to construct
    ///     that build TODAY. When that holds, the delta C(outgoing) - C(incoming) is exactly the engine's
    ///     own price difference, and any historical over- or under-payment is a constant offset that is
    ///     already baked into `current` and never enters the subtraction at all.
    ///
    ///     The WEAK argument - "the mispricing is a property of the skill, so it appears on both sides and
    ///     cancels" - is false, and this is the shape of its failure: SkillCreditsSpent charges
    ///     Trained+Specialized for a Specialized entry and Trained alone for a Trained one, so an error in
    ///     the upgrade term cancels only when the skill's SkillAdvancementClass MATCHES on both sides, and
    ///     SAC is per-BUILD. A stored row written before the player bought AugmentationSpecializeSalvaging
    ///     holds Salvaging Trained while the live character has it Specialized, so a generic price
    ///     contributes a raw 999 to the delta against an engine charge of zero, once per aug-spec skill,
    ///     against a lifetime budget in the low hundreds.
    ///
    ///     C == T is achieved by the cost DELEGATE, not by the arithmetic here:
    ///     Player.LookupSkillCreditCost is an instance method that prices an aug-specialized skill's
    ///     upgrade at zero, matching the engine. That cannot itself go asymmetric because an augmentation
    ///     is a GLOBAL character property, identical on both sides of any one switch.
    ///
    ///     One generic-versus-engine residual deliberately remains: ArcaneLore's heritage NormalCost of 0
    ///     (PlayerFactory.cs) against a dat TrainedCost of 4. It is harmless, and confirmed rather than
    ///     assumed to be the only one - a dat probe of all 13 heritage groups found ArcaneLore's the ONLY
    ///     skill-cost override anywhere in them, with its PrimaryCost equal to the generic upgrade cost, so
    ///     there is no specialization discount hiding in any heritage. It cannot bite because ArcaneLore is
    ///     AlwaysTrained and therefore Trained or Specialized in every build forever, so it is never
    ///     Untrained on one side of a swap and priced on the other.
    ///
    /// The delta form would be UNSAFE for skill credits if a global sink existed for them the way the
    /// vendor is one for ability points, because a sink outside the build makes `current` and the build's
    /// derived cost disagree permanently rather than transiently. Verified this session by grepping every
    /// write of Player.AvailableSkillCredits in Source/ACE.Server: the only decrements are
    /// Player_Skills.TrainSkill and Player_Skills.SpecializeSkill (both per-facet skill state, captured
    /// and restored by a switch), Player_Mule.ConvertToMule (a one-way, irreversible conversion that
    /// Player.CheckFacetGates now refuses outright via MuleBlocked), and admin/developer commands
    /// (AdminCommands, DeveloperCommands./delevel, DeveloperFixCommands). No vendor, no crafting recipe,
    /// no quest reward and no item spends skill credits. If that ever changes, this class must change with
    /// it - see AvailableSkillCreditsAfterSwap.
    ///
    /// The delta form conserves exactly ONLY under one further precondition, and it is the caller's job to
    /// enforce it: the resource released by switching away from a build must not be spendable on a sink
    /// that is not per-facet - attributes and vitals are global across every facet and have no refund
    /// path. If a switch is allowed to proceed while the incoming build cannot be fully paid for, the
    /// shortfall is silently absorbed into the zero clamp and the player keeps whatever they bought with
    /// the released resource AND the un-payable-for build once they switch back. This class cannot enforce
    /// that itself - it has no notion of a pending switch - so every *AfterSwap method reports the
    /// shortfall instead of hiding it, and the caller MUST refuse the switch outright whenever the
    /// shortfall is non-zero, before anything is mutated.
    ///
    /// This class takes no Player, touches no database and calls no DatManager, so it is unit-testable;
    /// the dat-backed credit costs arrive through a delegate.
    /// </summary>
    public static class FacetPools
    {
        /// <summary>
        /// Skill credits a build commits. A Specialized skill is charged the trained cost AND the
        /// upgrade cost, matching how the two are charged separately when the player buys them.
        ///
        /// The price comes entirely from <paramref name="costLookup"/>, and the delegate a caller passes
        /// is load-bearing: it MUST reproduce what the engine charged that character, not what the dat
        /// generically lists. Note the shape that makes a generic lookup unsafe here - a Specialized entry
        /// is charged Trained+Specialized and a Trained one only Trained, so a per-skill pricing error
        /// survives the subtraction in <see cref="AvailableSkillCreditsAfterSwap"/> whenever the skill's
        /// SAC differs between the two builds, which for an augmentation-specialized skill is ordinary.
        /// Player.LookupSkillCreditCost is the delegate that gets this right. Do NOT use this value on its
        /// own to decide what a character can afford.
        /// </summary>
        public static uint SkillCreditsSpent(IEnumerable<FacetSkillEntry> skills, Func<Skill, SkillCreditCost> costLookup)
        {
            if (skills == null || costLookup == null)
                return 0;

            ulong total = 0;

            foreach (var entry in skills)
            {
                if (entry == null || entry.Sac == SkillAdvancementClass.Untrained || entry.Sac == SkillAdvancementClass.Inactive)
                    continue;

                var cost = costLookup(entry.Skill);

                total += (ulong)Math.Max(0, cost.Trained);

                if (entry.Sac == SkillAdvancementClass.Specialized)
                    total += (ulong)Math.Max(0, cost.Specialized);
            }

            return total > uint.MaxValue ? uint.MaxValue : (uint)total;
        }

        /// <summary>
        /// Total experience a build has invested across every skill. Accumulated as ulong because each
        /// skill's PP is a uint and a high-level build sums well past uint.MaxValue.
        /// </summary>
        public static ulong TotalPp(IEnumerable<FacetSkillEntry> skills)
        {
            if (skills == null)
                return 0;

            ulong total = 0;

            foreach (var entry in skills)
            {
                if (entry != null)
                    total += entry.Pp;
            }

            return total;
        }

        /// <summary>
        /// The unspent experience pool after a switch: release what the outgoing build had committed,
        /// then commit what the incoming build needs. The return value is always clamped at zero - no
        /// caller can ever be handed a negative pool.
        ///
        /// <paramref name="shortfall"/> is set to how much XP the incoming build could NOT be paid for
        /// (zero when it was fully affordable). A non-zero shortfall means the switch MUST be refused by
        /// the caller before anything is mutated: proceeding anyway silently absorbs the deficit into the
        /// clamped zero, and the player ends up holding both the incoming skill build (Player_Facets
        /// applies it unconditionally) and whatever was bought with the released XP while away - most
        /// dangerously attributes or vitals, which are global across every facet and have no refund
        /// path. That is XP duplication, not loss.
        /// </summary>
        public static long AvailableExperienceAfterSwap(long current, ulong outgoingPp, ulong incomingPp, out ulong shortfall)
        {
            var released = current + (long)Math.Min(outgoingPp, long.MaxValue);
            var committed = released - (long)Math.Min(incomingPp, long.MaxValue);

            if (committed < 0)
            {
                shortfall = (ulong)(-committed);
                return 0;
            }

            shortfall = 0;
            return committed;
        }

        /// <summary>
        /// The unspent skill credit pool after a switch, in the same delta form
        /// <see cref="AvailableExperienceAfterSwap"/> uses, and for the same reason: a recompute from
        /// TotalSkillCredits minus the incoming build's derived cost is wrong in ordinary play, because
        /// <see cref="SkillCreditsSpent"/> prices augmentation-specialized skills and a heritage's free
        /// skill at their generic dat cost while the engine charged zero for them.
        ///
        /// The delta form is CORRECT here only in combination with a cost delegate satisfying
        /// C(B) == T(B): what <see cref="SkillCreditsSpent"/> computes for a build equals what the engine
        /// would charge to construct that build today. Under that equality the subtraction below IS the
        /// engine's own price difference, and any historical over- or under-payment is a constant offset
        /// already carried in <paramref name="current"/> that never enters the delta.
        ///
        /// Do NOT reach for the weaker argument - "the mispricing is a property of the skill, so it
        /// appears on both sides and cancels". It is false, and it once shipped a credit-duplication hole.
        /// An upgrade-term error cancels only when the skill's SAC matches on both sides, and SAC is
        /// per-build: <see cref="SkillCreditsSpent"/> charges Trained+Specialized for a Specialized entry
        /// and Trained alone for a Trained one, so an augmentation-specialized skill stored Trained in a
        /// row written before the aug was bought, and Specialized live, contributes a raw 999 to the delta
        /// against an engine charge of zero. Player.LookupSkillCreditCost restores C == T by pricing the
        /// aug-specialized upgrade at zero, which cannot itself go asymmetric because an augmentation is a
        /// global character property. ArcaneLore's free heritage training is the one confirmed residual
        /// (the only skill-cost override in any of the 13 heritage groups) and it cannot bite, because
        /// AlwaysTrained means it is never Untrained on one side and priced on the other.
        ///
        /// The delta form is SAFE here because skill credits have no sink outside the build - unlike class
        /// ability points, which a vendor can debit. See the class summary for the grep that establishes
        /// that absence, and treat any new global spend of AvailableSkillCredits as a change that
        /// invalidates this method.
        ///
        /// <paramref name="shortfall"/> is how many credits the incoming build could not be paid for. As
        /// with experience, a non-zero shortfall MUST make the caller refuse the switch before anything is
        /// mutated; the zero clamp exists so the client is never handed a negative count, not as a licence
        /// to proceed. It is a long rather than an int because both spends are uint and their difference
        /// does not fit an int in the pathological case.
        /// </summary>
        public static int AvailableSkillCreditsAfterSwap(int current, uint outgoingSpent, uint incomingSpent, out long shortfall)
        {
            var committed = (long)current + outgoingSpent - incomingSpent;

            if (committed < 0)
            {
                shortfall = -committed;
                return 0;
            }

            shortfall = 0;
            return committed > int.MaxValue ? int.MaxValue : (int)committed;
        }

        /// <summary>
        /// The unspent class ability point pool after a switch, in the same delta form
        /// <see cref="AvailableExperienceAfterSwap"/> uses.
        ///
        /// A recompute from TotalClassAbilityPointsEarned minus the incoming build's spend is wrong in
        /// ordinary play, because learned abilities are NOT the only sink: the Class Ability Point vendor
        /// currency (Player_Bank) and prepaid training vouchers (Player_ClassAbilityTokens) both debit
        /// AvailableClassAbilityPoints while leaving TotalClassAbilityPointsEarned alone. Re-deriving the
        /// pool restores every one of those spends, for free, on every switch. The delta form never
        /// re-derives, so a vendor spend simply stays spent.
        ///
        /// <paramref name="shortfall"/> is how many points the incoming build could not be paid for, and a
        /// non-zero value MUST make the caller refuse the switch before anything is mutated - the same
        /// contract, and the same reason, as the other two pools.
        /// </summary>
        public static int AvailableClassAbilityPointsAfterSwap(int current, int outgoingSpent, int incomingSpent, out int shortfall)
        {
            var committed = (long)current + outgoingSpent - incomingSpent;

            if (committed < 0)
            {
                var deficit = -committed;
                shortfall = deficit > int.MaxValue ? int.MaxValue : (int)deficit;
                return 0;
            }

            shortfall = 0;
            return committed > int.MaxValue ? int.MaxValue : (int)committed;
        }
    }
}
