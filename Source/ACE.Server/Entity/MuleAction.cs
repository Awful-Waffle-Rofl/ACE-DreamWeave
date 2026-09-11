namespace ACE.Server.Entity
{
    /// <summary>
    /// One member per capability a mule is barred from. Every member MUST have at least one guard site in
    /// ACE.Server outside Player_Mule.cs / MuleAction.cs - MuleSystemTests.EveryMuleActionHasAGuardSite
    /// enforces that, so a member added here without a guard fails the build's test run rather than shipping
    /// as a silently unenforced restriction.
    /// </summary>
    public enum MuleAction
    {
        GainExperience,
        GainLuminance,
        AdvanceQuest,
        TrainClassAbility,

        /// <summary>
        /// Being awarded class ability points at all, as distinct from spending them on a rank. Needed
        /// separately because the milestone grant is derived from Level alone and runs on every login: a mule
        /// is level 180 by fiat, so without this it is immediately paid every level milestone and shown the
        /// first-class-ability-point notice for points it can never spend.
        /// </summary>
        GainClassAbilityPoints,
        Craft,
        Salvage,
        MeleeAttack,
        MissileAttack,
        CastSpell,
        JoinFellowship,
        SwearAllegiance,
        ChangePkStatus,
        AcceptContract,
        GainTitle,
        BuyHouse,
        StartChallenge,

        /// <summary>
        /// Switching to another Player Facet slot. A mule has TotalSkillCredits 0 and every untrainable
        /// skill forced to Untrained (Player_Mule), so letting one switch would capture that zeroed state
        /// into a slot row as if it were a build. Unreachable at stock config, because mule_level (180) is
        /// below facet_slot2_level (300), but both are live tunables and neither knows about the other.
        /// </summary>
        ChangeFacet
    }
}
