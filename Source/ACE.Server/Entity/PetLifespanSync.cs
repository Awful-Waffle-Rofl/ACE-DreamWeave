namespace ACE.Server.Entity
{
    /// <summary>
    /// Keeps an owner's combat pets on ONE expiry clock, so a single summoning-essence charge always buys a
    /// full-duration PAIR when the owner holds Summon 2x (WorldObjects/Pet.cs, Init).
    ///
    /// Why this is needed. A combat pet's lifespan (43 s on every combat-pet weenie) is only ever evaluated
    /// on that pet's OWN heartbeat - WorldObject.Heartbeat checks IsLifespanSpent - and
    /// WorldObject.InitializeHeartbeats phases every object's first heartbeat by a random 0-5 s to spread
    /// server load. Two pets summoned in the same activation therefore expire up to 5 s apart, anywhere in
    /// a 43-48 s window, and the essence's 45 s CooldownDuration sits inside that window. So the usual
    /// outcome is that the owner comes off cooldown holding exactly ONE pet: the free slot can then only be
    /// topped up one pet at a time (PetDevice.ActOnUse's second summon requires the secondary slot free),
    /// and from then on the two pets are a whole cooldown apart - two pets for a few seconds per cycle and
    /// one pet for the rest of it.
    ///
    /// Pure - no WorldObject or Player dependency - so the slot selection and the guards are unit-testable
    /// without a live world, matching <see cref="PetAssistTargeting"/> and <see cref="PetFollowRules"/>.
    /// </summary>
    public static class PetLifespanSync
    {
        /// <summary>
        /// The pet in the owner's OTHER slot, i.e. the one whose expiry clock may need rewriting after
        /// <paramref name="justSummoned"/> took a slot. Null when there is no other pet.
        ///
        /// Reference identity rather than a slot flag, because Pet.Init has already written itself into
        /// whichever slot was free and does not record which one that was.
        /// </summary>
        public static T SelectSibling<T>(T justSummoned, T primarySlot, T secondarySlot) where T : class
        {
            if (justSummoned == null)
                return null;

            var sibling = ReferenceEquals(primarySlot, justSummoned) ? secondarySlot : primarySlot;

            // defensive: a slot still holding the new pet under both reads leaves nothing to sync
            return ReferenceEquals(sibling, justSummoned) ? null : sibling;
        }

        /// <summary>
        /// Whether the sibling's expiry clock may be re-pointed at the freshly summoned pet's.
        ///
        /// Combat pets only, on both sides: a passive pet's lifespan is cleared at summon time
        /// (Pet.Init sets TimeToRot = -1 and passive pet weenies carry no Lifespan), and the two-pet pairing
        /// is a combat-pet rule. A destroyed sibling is skipped because its slot is about to be cleared
        /// anyway (WorldObject.Destroy).
        /// </summary>
        public static bool ShouldSync(bool justSummonedIsCombatPet, bool siblingExists, bool siblingIsCombatPet, bool siblingDestroyed)
        {
            return justSummonedIsCombatPet && siblingExists && siblingIsCombatPet && !siblingDestroyed;
        }

        /// <summary>
        /// Whether adopting the new pet's clock would leave the sibling expiring no EARLIER than it already
        /// does. The sync is a correction, never a nerf, so the caller declines when this is false and the
        /// sibling simply keeps the clock it has.
        ///
        /// Needed because the new pet being newer does not by itself guarantee a later deadline once
        /// Empowered Summons' Loyalty duration rider is in play: the rider is read at summon time, so an
        /// owner whose Loyalty buff lapsed between two summons produces a NEWER pet with a SHORTER Lifespan.
        /// A pet slain seconds after a Summon 2x activation, replaced through Soul Tether's free resummon,
        /// is the case that actually reaches it.
        ///
        /// Deadlines are compared as long, not int: both terms are seconds-since-epoch plus a lifespan, and
        /// the sum can exceed int.MaxValue.
        /// </summary>
        public static bool ExtendsSibling(int newCreationTimestamp, int newLifespan, int siblingCreationTimestamp, int siblingLifespan)
        {
            return (long)newCreationTimestamp + newLifespan >= (long)siblingCreationTimestamp + siblingLifespan;
        }
    }
}
