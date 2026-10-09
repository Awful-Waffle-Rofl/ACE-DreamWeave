namespace ACE.Server.Entity
{
    /// <summary>
    /// Whether activating a combat-pet essence should retire the owner's active combat pet(s) and summon a
    /// fresh set, instead of refusing with "is already active" (PetDevice.CheckUseRequirements,
    /// Pet.HandleCurrentActivePet_Replace/_Retail) or silently adding a second one alongside the first.
    ///
    /// Owner ruling (2026-09-23): using ANY combat-pet essence - the same one already out, or a different
    /// one, with or without the Summon 2x class ability - while one or two of the owner's combat pets from an
    /// earlier summon are still alive ALWAYS retires every active combat pet and summons a fresh set (1 pet
    /// normally, 2 with Summon 2x). The essence's own 45 s cooldown is the only gate; nothing about which
    /// wcid is already out, or which wcid is being summoned, matters any more. This replaces the older
    /// "swap only on a DIFFERENT essence, same essence still refuses" rule from #1129 (115a24177), which was
    /// aimed at a narrower problem: Empowered Summons' Loyalty duration rider can stretch a combat pet's
    /// Lifespan to well past the flat 45 s essence cooldown (see PetLifespanSync's doc comment for the
    /// numbers), so client automation that retried a refused activation could sit in a retry loop until the
    /// old pet finally expired on its own. The owner ruling widens the fix to cover the same-essence case too,
    /// rather than leaving it refusing.
    ///
    /// Pure - no WorldObject or Player dependency - so the decision is unit-testable without a live world,
    /// matching <see cref="PetLifespanSync"/>.
    /// </summary>
    public static class CombatPetRetireRules
    {
        /// <summary>
        /// TRUE iff this is the FIRST summon of a combat-pet-essence activation (<paramref name="isFirstSummonOfActivation"/>,
        /// i.e. Init's spawnStagger is false) AND at least one of the owner's active combat-pet slots is
        /// populated. A null slot wcid means that slot is empty, destroyed, or not holding a combat pet.
        ///
        /// Always FALSE for the SECOND summon of a Summon 2x activation (isFirstSummonOfActivation false):
        /// that pet is going into the free slot alongside the one this same activation just placed, and must
        /// never retire it - callers gate that case on <see cref="ACE.Server.WorldObjects.Player.CanSummonAdditionalCombatPet"/>
        /// instead, checked AFTER this rule declines.
        /// </summary>
        public static bool ShouldRetireActivePets(bool isFirstSummonOfActivation, uint? primaryCombatPetWcid, uint? secondaryCombatPetWcid)
        {
            if (!isFirstSummonOfActivation)
                return false;

            return primaryCombatPetWcid != null || secondaryCombatPetWcid != null;
        }
    }
}
