namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// WaffleACE World Events (WP-23, D6 - pure): the rule Monster_Tick enforces for an objective creature
    /// (a Rift or Element Portal pillar - PropertyBool 9026 WorldEventObjective). Split out as a static so
    /// it is testable without a live Creature (no landblock, no dat, no world) - Monster_Tick itself just
    /// calls this and returns early when it says to.
    ///
    /// TargetingTactic.None does NOT achieve "never thinks": Monster_Awareness substitutes
    /// Random|TopDamager for None once a hit wakes the creature, so an objective creature flagged only that
    /// way still finds a target, moves and attacks. This flag is the actual enforcement point.
    /// </summary>
    public static class WorldEventObjectiveRules
    {
        /// <summary>
        /// True when Monster_Tick must return immediately without searching for a target, moving or
        /// attacking - i.e. whenever the creature is flagged WorldEventObjective. IsDead is handled by
        /// Monster_Tick's earlier, unconditional check and is not part of this rule.
        /// </summary>
        public static bool SkipsMonsterTick(bool worldEventObjective)
        {
            return worldEventObjective;
        }
    }
}
