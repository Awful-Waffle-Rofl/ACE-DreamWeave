using System;

using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// goals.json type "KillBoss" (TECH-DESIGN 2.5, PLAN 1.8). Completes only when the guid reported dead
    /// equals WorldEventSpawner.BossGuid, and BossGuid is non-zero (0 means the champion has not spawned
    /// yet - see WorldEvent.SpawnChampion, which only ever assigns it once).
    /// </summary>
    public sealed class KillBossObjective : IWorldEventObjective
    {
        private readonly GoalDef goal;
        private readonly Func<uint> bossGuid;

        /// <summary>
        /// The boss's own name, for a NAMED boss (BOSS-STANDARD.md). Null - the family-champion case and
        /// every existing call site - keeps the generic "The champion" wording, because a champion picked
        /// from a family roster has no name the progress line could use.
        /// </summary>
        private readonly string bossName;

        private bool bossDead;

        private string lastKillerName;
        private string topDamagerName;

        public KillBossObjective(GoalDef goal, Func<uint> bossGuid) : this(goal, bossGuid, null)
        {
        }

        public KillBossObjective(GoalDef goal, Func<uint> bossGuid, string bossName)
        {
            this.goal = goal;
            this.bossGuid = bossGuid ?? (() => 0);
            this.bossName = string.IsNullOrWhiteSpace(bossName) ? null : bossName.Trim();
        }

        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            OnDeathCore(creature?.Guid.Full ?? 0, WorldEventMvpResolver.ResolveName(lastDamager),
                WorldEventMvpResolver.ResolveName(topDamager));
        }

        /// <summary>Testable core (D6). A guid that is not the current boss guid (including 0) is ignored.</summary>
        public void OnDeathCore(uint guid, string lastDamagerName, string topDamagerName)
        {
            var boss = bossGuid();

            if (boss == 0 || guid != boss)
                return;

            bossDead = true;
            lastKillerName = lastDamagerName;
            this.topDamagerName = topDamagerName;
        }

        public void Tick(double now)
        {
        }

        public bool IsComplete => bossDead;

        public string ProgressText
        {
            get
            {
                var who = bossName ?? "The champion";

                if (bossGuid() == 0)
                    return bossName == null
                        ? "the champion has not yet appeared"
                        : $"{who} has not yet appeared";

                return bossDead ? $"{who} has fallen." : $"{who} still stands.";
            }
        }

        /// <summary>Killing blow plus top damager on the boss, when different (PLAN 1.8).</summary>
        /// <summary>
        /// WP-20: null - "no opinion". The champion is the objective; trash deaths do not advance it, so
        /// this must never suppress a wave (see IWorldEventObjective.RemainingKills).
        /// </summary>
        public int? RemainingKills => null;

        public WorldEventMvp Mvp()
        {
            return WorldEventMvpResolver.KillerPlusTopDamage(lastKillerName, topDamagerName,
                "killing blow", "struck the killing blow, top damage: {0}");
        }
    }
}
