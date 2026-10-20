using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The finale boss controller (protect.bossTargetsProtected; the rules are in WorldEventFinaleBoss). Once a second, on
    /// the world thread: names and keeps the boss on one protected target, swaps it to another every bossSwapSeconds to
    /// bossSwapMaxSeconds, lets a damage pull or a taunt hold it on a player, and lets it go for the players once no target
    /// stands. Every change to the boss itself runs on its own landblock's queue.
    /// </summary>
    public sealed partial class WorldEvent
    {
        /// <summary>
        /// Hold protect.clearArea: where the portals stand after the spawn-area check (WorldEventSpawnArea), moved or skipped
        /// around obstacles. Null when the goal has no check: the portals stand where protect.portals puts them.
        /// </summary>
        internal IReadOnlyList<PortalSpot> HoldPortalSpots { get; set; }

        private uint holdBossGuid;
        private string holdBossName;
        private uint holdBossTargetGuid;
        private string holdBossTargetName;
        private double holdBossNextSwapAt;
        private double holdBossNextTickAt;
        private uint holdBossPulledBy;
        private bool holdBossTaunted;
        private bool holdBossAnnouncedHit;
        private bool holdBossOnPlayers;
        private readonly Random holdBossRng = new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

        /// <summary>
        /// The boss landed a hit on a protected target (its target's landblock thread): the first hit on each new target is
        /// logged at Info, the rest at Debug, so a "the boss never touches the envoys" report can be read off the log.
        /// </summary>
        internal void NoteFinaleBossHit(Creature boss, Creature target, float dealt, double now)
        {
            var first = !holdBossAnnouncedHit;
            holdBossAnnouncedHit = true;

            var line = $"[WORLDEVENT] run={RunId} finale boss {boss?.Name} hit {target?.Name} for {dealt:0} " +
                       $"(health {target?.Health.Current}/{target?.Health.MaxValue}, cap {Composition?.Goal?.Protect?.BossMinKillSeconds:0} s alone)";

            if (first)
                log.Info(line);
            else
                log.Debug(line);
        }

        /// <summary>Forgets the boss (a new boss, or none).</summary>
        private void ResetHoldFinaleBoss(uint guid)
        {
            holdBossGuid = guid;
            holdBossName = null;
            holdBossTargetGuid = 0;
            holdBossTargetName = null;
            holdBossNextSwapAt = 0;
            holdBossPulledBy = 0;
            holdBossTaunted = false;
            holdBossAnnouncedHit = false;
            holdBossOnPlayers = false;
        }

        private void TickHoldFinaleBoss(double now)
        {
            try
            {
                var protect = Composition?.Goal?.Protect;

                if (Composition?.Goal?.TypeKind != GoalType.Hold || protect == null || !protect.BossTargetsProtected)
                    return;

                if (now < holdBossNextTickAt)
                    return;

                holdBossNextTickAt = now + 1.0;

                var boss = Spawner.LiveWaveCreatures.FirstOrDefault(c => c != null && c.WorldEventFinaleBoss && !c.IsDead && !c.IsDestroyed);

                if (boss == null)
                {
                    if (holdBossGuid != 0)
                        ResetHoldFinaleBoss(0);

                    return;
                }

                if (boss.Guid.Full != holdBossGuid)
                {
                    ResetHoldFinaleBoss(boss.Guid.Full);
                    holdBossName = boss.Name;
                }

                var landblock = boss.CurrentLandblock;

                if (landblock == null)
                    return;

                var targets = Spawner.LiveProtectedCreatures.Where(t => t != null && !t.IsDead && !t.IsDestroyed).ToList();

                // No target stands: the boss fights the players (its seeker flag clears itself).
                if (targets.Count == 0)
                {
                    if (!holdBossOnPlayers)
                        log.Info($"[WORLDEVENT] run={RunId} finale boss {holdBossName}: no target stands; it turns on the players");

                    holdBossOnPlayers = true;
                    holdBossTargetGuid = 0;
                    return;
                }

                var unixNow = Time.GetUnixTime();

                // A damage pull: enough player damage within the window holds it on the top damager.
                var (recent, top) = WorldEventFinaleBoss.RecentDamage(boss.WorldEventBossDamageSnapshot(), unixNow, protect.BossPullWindowSeconds);
                var puller = WorldEventFinaleBoss.PullDue(recent, boss.Health.MaxValue, protect.BossPullShare) ? PlayerManager.GetOnlinePlayer(top) : null;

                if (puller != null && !puller.IsDead && puller.PhysicsObj != null)
                {
                    if (holdBossPulledBy != puller.Guid.Full)
                    {
                        log.Info($"[WORLDEVENT] run={RunId} finale boss {holdBossName} pulled off {holdBossTargetName ?? "its target"} by {puller.Name} " +
                                 $"({recent:0} damage in {protect.BossPullWindowSeconds:0} s, {recent / Math.Max(1u, boss.Health.MaxValue) * 100:0.#}% of its health); " +
                                 $"held {protect.BossPullSeconds:0} s");

                        var pulledLine = ComposeBossLine(protect.BossPulledAnnounce, t => WorldEventFinaleBoss.PulledLine(t, holdBossName, puller.Name));

                        if (!string.IsNullOrEmpty(pulledLine))
                            WorldEventAnnouncer.Local(this, pulledLine);
                    }

                    holdBossPulledBy = puller.Guid.Full;

                    var until = unixNow + protect.BossPullSeconds;
                    var b = boss;
                    var p = puller;

                    landblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        if (b.IsDead || b.IsDestroyed)
                            return;

                        b.WorldEventBossPullTarget = p;
                        b.WorldEventBossPullUntil = until;
                        b.TrySelectWorldEventSeekTarget();
                    }));

                    return;
                }

                // A taunt holds it too (stock taunt targeting); nothing to do but note it.
                var taunter = boss.TauntTarget;
                var taunted = taunter != null && ACE.Server.Entity.Timers.RunningTime < boss.TauntExpiry && !taunter.IsDead;

                if (taunted != holdBossTaunted)
                {
                    log.Info(taunted
                        ? $"[WORLDEVENT] run={RunId} finale boss {holdBossName} taunted onto {taunter.Name}"
                        : $"[WORLDEVENT] run={RunId} finale boss {holdBossName}: the taunt ended");

                    holdBossTaunted = taunted;
                }

                if (taunted)
                    return;

                // A pull still running holds it on that player.
                if (boss.WorldEventBossPullTarget != null && unixNow < boss.WorldEventBossPullUntil)
                    return;

                var returning = holdBossPulledBy != 0;
                holdBossPulledBy = 0;

                // The protected target: keep it, or swap to the healthiest of the others when the timer is up. Back from a
                // pull it turns on a different one too, so pulling it off a knight spares that knight.
                var swap = holdBossTargetGuid != 0 && (now >= holdBossNextSwapAt || returning);
                var health = targets.Select(t => (t.Guid.Full, t.Health.Current, t.Health.MaxValue)).ToList();
                var pick = WorldEventFinaleBoss.PickTarget(health, holdBossTargetGuid, swap);
                var target = targets.FirstOrDefault(t => t.Guid.Full == pick);

                if (target == null)
                    return;

                var changed = pick != holdBossTargetGuid;

                if (changed || swap || holdBossNextSwapAt == 0)
                    holdBossNextSwapAt = now + WorldEventFinaleBoss.SwapDelay(holdBossRng, protect.BossSwapSeconds, protect.BossSwapMaxSeconds);

                if (changed || returning)
                {
                    log.Info($"[WORLDEVENT] run={RunId} finale boss {holdBossName} turns on {target.Name} " +
                             $"({WorldEventHoldRules.HealthPercent(target.Health.Current, target.Health.MaxValue)}%; " +
                             $"{(returning ? "back from a pull" : swap ? "swap" : holdBossTargetGuid == 0 ? "healthiest" : "its target fell")}); " +
                             $"next swap in {holdBossNextSwapAt - now:0} s");

                    var line = ComposeBossLine(protect.BossTargetAnnounce, t => WorldEventFinaleBoss.TargetLine(t, holdBossName, target.Name));

                    if (!string.IsNullOrEmpty(line))
                        AnnounceInRun(WorldEventAnnouncer.Global(line));

                    holdBossAnnouncedHit = false;
                }

                holdBossTargetGuid = pick;
                holdBossTargetName = target.Name;

                var bossRef = boss;
                var targetGuid = pick;

                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (bossRef.IsDead || bossRef.IsDestroyed)
                            return;

                        bossRef.WorldEventBossPullTarget = null;
                        bossRef.WorldEventSeeker = true;
                        bossRef.WorldEventSeekHealthiest = false;
                        bossRef.WorldEventSeekForcedGuid = targetGuid;

                        if (bossRef.AttackTarget?.Guid.Full != targetGuid && bossRef.TrySelectWorldEventSeekTarget()
                            && bossRef.MonsterState != Creature.State.Awake)
                            bossRef.WakeUp(alertNearby: false);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={RunId} finale boss retarget threw", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} finale boss tick threw", ex);
            }
        }

        /// <summary>One line from <paramref name="pool"/> (or the default), filled by <paramref name="fill"/>, with the family tokens.</summary>
        private string ComposeBossLine(IReadOnlyList<string> pool, Func<string, string> fill)
        {
            string template = null;

            if (pool != null && pool.Count > 0)
            {
                lock (protectedLineRngSync)
                    template = WorldEventHoldRules.PickLine(pool, protectedLineRng);
            }

            var filled = fill(template);

            return ComposeProtectedLine(new List<string> { filled }, null) ?? filled;
        }
    }
}
