namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Chat text for Runic Ward, kept out of Player_ClassAbilityPreWriteBuffs.cs as pure statics for the
    /// same reason <see cref="RunicWardMath"/> is: unit-testable without a live Player.
    ///
    /// ONE LINE PER WARD LIFETIME, not one per hit - the per-hit absorb line this ability shipped with was
    /// the loudest complaint in beta feedback, so the pool's whole story (how much it ate, in total) is told
    /// exactly once, at the moment the lifetime ends: <see cref="ShatterMessage"/> when an absorb empties it,
    /// <see cref="FadeMessage"/> when it lapses unused, or the unchanged discharge line when it is spent. The
    /// "fully inscribed" notice is the one exception still fired mid-lifetime, and even that is capped at
    /// once - see <see cref="ShouldAnnounceFullyInscribed"/>.
    /// </summary>
    public static class RunicWardMessages
    {
        /// <summary>Unchanged from the ward's original notice - fires at most once per lifetime.</summary>
        public static string FullyInscribedMessage(uint amount) =>
            $"Your runic ward is fully inscribed, absorbing your next {amount:N0} points of damage.";

        /// <summary>An absorb that drains the pool to zero. <paramref name="totalAbsorbed"/> is the whole lifetime's take, not just this hit's.</summary>
        public static string ShatterMessage(uint totalAbsorbed) =>
            $"Your runic ward shatters after absorbing {totalAbsorbed:N0} points of damage.";

        /// <summary>
        /// The heartbeat lapse notice. Carries the lifetime total when the ward actually blocked something,
        /// and falls back to the bare original line when it sat unused the whole time it was up.
        /// </summary>
        public static string FadeMessage(uint totalAbsorbed) =>
            totalAbsorbed > 0
                ? $"Your runic ward fades after absorbing {totalAbsorbed:N0} points of damage."
                : "Your runic ward fades.";

        /// <summary>Unchanged from the ward's original notice.</summary>
        public static string DischargeMessage(uint spent, string spellName) =>
            $"Your runic ward discharges {spent:N0} points of damage into {spellName}!";

        /// <summary>
        /// TRUE only on the hit that first takes the pool from below cap to at-or-above it, and only if
        /// nothing has already announced that for the CURRENT lifetime. A ward drained by an absorb and then
        /// topped back up within the same lifetime must not announce a second time - see the caller for how
        /// <paramref name="alreadyAnnouncedThisLifetime"/> gets reset only at a lifetime boundary.
        /// </summary>
        public static bool ShouldAnnounceFullyInscribed(uint after, uint cap, bool alreadyAnnouncedThisLifetime) =>
            cap > 0 && after >= cap && !alreadyAnnouncedThisLifetime;
    }
}
