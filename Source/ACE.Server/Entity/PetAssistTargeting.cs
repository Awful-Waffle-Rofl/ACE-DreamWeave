namespace ACE.Server.Entity
{
    /// <summary>What a combat pet does with its target on one tick (CombatPet.HandleFindTarget).</summary>
    public enum PetTargetAction
    {
        /// <summary>Keep the current target.</summary>
        Keep,

        /// <summary>Switch to the monster the owner most recently attacked.</summary>
        SwitchToAssist,

        /// <summary>The current target is gone or invalid: fall back to the nearest-monster pick.</summary>
        FindNearest,
    }

    /// <summary>
    /// Summon assist (/summonattackontarget) target selection for a combat pet. Pure, so the decision is
    /// unit-testable without a live Player or pet; CombatPet.HandleFindTarget gathers the inputs.
    /// </summary>
    public static class PetAssistTargeting
    {
        /// <param name="assistOn">the owner's SummonAttackOnTarget setting</param>
        /// <param name="assistTargetValid">the owner's most recently attacked monster exists and passes this
        /// pet's target filter (alive, visible to the pet, attackable, faction rule, inside chase range)</param>
        /// <param name="assistTargetIsCurrent">that monster is already the pet's AttackTarget</param>
        /// <param name="currentTargetValid">the pet's current target is non-null, alive and visible to it -
        /// the same test HandleFindTarget has always used to decide whether to retarget</param>
        /// <param name="attackAnimating">the pet is mid swing or cast</param>
        public static PetTargetAction Decide(bool assistOn, bool assistTargetValid, bool assistTargetIsCurrent, bool currentTargetValid, bool attackAnimating)
        {
            if (assistOn && assistTargetValid && !assistTargetIsCurrent)
            {
                // let a swing or cast already playing finish on the target it started against; the switch
                // happens on the first tick after it. A dead or invalid current target is abandoned at once.
                if (currentTargetValid && attackAnimating)
                    return PetTargetAction.Keep;

                return PetTargetAction.SwitchToAssist;
            }

            return currentTargetValid ? PetTargetAction.Keep : PetTargetAction.FindNearest;
        }
    }
}
