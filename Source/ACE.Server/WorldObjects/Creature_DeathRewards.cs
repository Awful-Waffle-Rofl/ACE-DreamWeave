namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// Zeroes everything this creature's death path pays from, on this instance only (a speed-course creature:
        /// ThreadDungeonSpawner.ShapeCreature with Rewards = false). Traced against Creature_Death.cs /
        /// Creature_LootRolls.cs:
        ///   * XP - OnDeath_GrantXP pays (XpOverride ?? 0) x damage share, so XpOverride 0 pays 0;
        ///   * luminance - OnDeath_GrantXP pays LuminanceAward only when it is non-null, so it is removed;
        ///   * the treasure roll - GenerateTreasure rolls DeathTreasure, which is DeathTreasureOverride ?? the
        ///     cached TreasureDeath for DeathTreasureType; with both cleared it is null and nothing is rolled. The
        ///     other two corpse sources (create-list rows and DestinationType-tagged inventory) are stripped by the
        ///     Threads standardisation block, whatever this switch says;
        ///   * salvage affinities - GenerateTreasure rolls P_DungeonSalvageAffinities (and only with a run stamp);
        ///     cleared here, and the Rewards branch that would set it is skipped;
        ///   * rares - CreateCorpse recomputes CanGenerateRare at death from ResolveCanGenerateRare(current,
        ///     LootGateLevelFor(LootGateLevel, Level), ...): true at gate level 100+, or when the gate level is
        ///     above the player killer's level, otherwise the CURRENT value. Gate level 0 (it outranks Level) meets
        ///     neither for any player, and current is false, so no corpse reaches TryGenerateRare.
        /// NOT covered: the weenie's own KillQuest/KillQuest2/KillQuest3 kill-task strings (the speed-course spawner
        /// strips those itself), the killer's on-kill hooks in OnDeath (class abilities, weapon mods, kill-fill
        /// vessels, mule-form tokens), the CreatureKills counter, and the Marae Lassel treasure map / Relaria trophy
        /// inside CreateCorpse.
        /// <para/>
        /// Lives outside ThreadDungeons/ on purpose: Threads must never write the loot gate
        /// (WorldEventLootGateTests guards that folder), and this is a death-reward strip, not a Threads rule.
        /// </summary>
        public void StripDeathRewards()
        {
            XpOverride = 0;
            LuminanceAward = null;
            DeathTreasureOverride = null;
            DeathTreasureType = null;
            P_DungeonSalvageAffinities = null;
            LootGateLevel = 0;
            CanGenerateRare = false;
        }

        /// <summary>
        /// True for a creature a speed course spawned (LiveSpeedCourseWorld.Spawn). In-memory only, never persisted (the
        /// P_ back-reference convention, like P_WaveOwner): the instance and its creatures go away with the run. Read by
        /// OnDeath through <see cref="FeedsKillProgressHooks"/>.
        /// </summary>
        public bool P_SpeedCourseCreature;

        /// <summary>
        /// Whether a death feeds the killer's kill-fill vessel charges and mule-form token progress (OnDeath). False for a
        /// speed-course creature: a speed course is not a kill farm. Deliberately NOT IsRewardlessDeath: that predicate
        /// closes every on-kill hook and the corpse, while a speed kill keeps the class-ability and weapon-mod hooks,
        /// which are combat effects rather than progress, and its corpse.
        /// </summary>
        public static bool FeedsKillProgressHooks(bool speedCourseCreature) => !speedCourseCreature;
    }
}
