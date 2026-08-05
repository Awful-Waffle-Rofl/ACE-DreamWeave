using ACE.Entity.Enum.Properties;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// FORK ADDITION - the SCALAR half of Drain Health's fellowship-surplus gate, as pure predicates.
    ///
    /// Lives under ClassAbilities, not Entity: an ability's pure math belongs beside its handler, and this
    /// was one of only two files that did not follow that rule.
    ///
    /// These are the conditions that depend on nothing but the spell shape and a handful of facts about the
    /// caster, so they can be tested without a live Player (Player's static initializer cannot run under the
    /// test host). The per-recipient conditions - alive, same landblock, within range, actually missing
    /// health - need live world state and stay in WorldObject.GetDrainSurplusFellows, which calls these first.
    ///
    /// A RECIPIENT IS A FELLOW OR ONE OF THE CASTER'S OWN SUMMONS (user, 2026-08-03: "Transfusion should also
    /// work on player summons in range"). That is why the caster gate below is an OR rather than a fellowship
    /// requirement: a solo Blood Mage with a hurt pet beside them is a real cascade target.
    ///
    /// The cascade itself is the Blood Mage class ability "Transfusion" (ClassAbilityId.Transfusion), a
    /// single-rank T1 entry. Rank 0 means Drain behaves exactly as retail: bounded by the caster's own
    /// missing health, which is ZERO damage at full health. That is intended, not a bug - see the doc
    /// comment on GetDrainSurplusFellows before "fixing" it for everyone.
    /// </summary>
    public static class DrainSurplusEligibility
    {
        /// <summary>
        /// Is this spell the right SHAPE for the cascade? Health drains only.
        ///
        /// The Source check matches the existing crit gate in HandleCastSpell_Transfer. The Destination
        /// check is what makes it safe to add missing HEALTH to maxDestVitalChange, which for any other
        /// transfer spell is a missing-mana or missing-stamina figure. Stamina and Mana drains, and every
        /// non-drain transfer (Stamina to Mana, Infuse Mana Other, ...), keep retail behaviour untouched.
        ///
        /// Checked FIRST by the caller, so a non-drain transfer never pays for a class-ability or
        /// fellowship lookup.
        /// </summary>
        public static bool SpellQualifies(bool isDrain, PropertyAttribute2nd source, PropertyAttribute2nd destination)
        {
            return isDrain
                && source == PropertyAttribute2nd.Health
                && destination == PropertyAttribute2nd.Health;
        }

        /// <summary>
        /// Is this CASTER entitled to the cascade?
        ///
        ///  - casterIsPlayer / targetIsEligibleVictim: PLAYER-CAST AND PvE ONLY, the same gate shape as
        ///    TryLifeCriticalHit and GetLifeCasterMods. Monsters casting Drain are completely unaffected,
        ///    and this never becomes a PvP lever.
        ///  - casterIsTransferDestination: the caster is the one receiving the transfer, so "surplus the
        ///    caster cannot absorb" is a meaningful quantity. Always true for a real Drain (the spell
        ///    carries TransferFlags.CasterDestination), checked so it cannot silently stop being true.
        ///  - transfusionRank: the class gate. Single rank, so this is on or off - there is nothing to
        ///    scale, and any rank >= 1 behaves identically.
        ///  - hasFellowship OR hasSummon: there must be SOMEBODY to cascade to. Before 2026-08-03 this was
        ///    hasFellowship alone, so a solo Blood Mage got no cascade whatever they had standing next to
        ///    them; summons now satisfy the same condition on their own.
        ///
        /// BOTH OF THE LAST TWO ARE PRESENCE, NOT ELIGIBILITY. hasFellowship has always meant "a fellowship
        /// object exists", not "a fellow is alive, near and hurt", and hasSummon deliberately mirrors it:
        /// "an active summon exists". The per-recipient filters run afterwards at the call site, and a caster
        /// who passes here but has nobody actually eligible still ends up with an empty recipient list, which
        /// is the same retail-identical path as failing this gate. Keeping the two parameters at the same
        /// granularity is what lets this stay a pure predicate over cheap facts.
        ///
        /// hasSummon must mean the CASTER'S OWN summons only. A fellow's pet is not a recipient - the call
        /// site reads Player.CurrentActivePet / Player.SecondaryActivePet off the caster and nobody else.
        /// </summary>
        public static bool CasterQualifies(bool casterIsPlayer, bool casterIsTransferDestination, bool targetIsEligibleVictim, int transfusionRank, bool hasFellowship, bool hasSummon)
        {
            return casterIsPlayer
                && casterIsTransferDestination
                && targetIsEligibleVictim
                && transfusionRank >= 1
                && (hasFellowship || hasSummon);
        }
    }
}
