using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>Weenie names for a forced boss (world_events_hold_finale_boss_wcid) that is not in the roster.</summary>
    internal static class WorldEventHoldTuningNames
    {
        internal static Func<uint, string> NameLookup = wcid => ACE.Database.DatabaseManager.World.GetCachedWeenie(wcid)?.GetProperty(ACE.Entity.Enum.Properties.PropertyString.Name);

        public static string WeenieName(uint wcid)
        {
            try
            {
                return wcid == 0 ? null : NameLookup(wcid);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>World Events Hold: the live admin settings that act on a run (HoldSettings) and need the run's own state.</summary>
    public sealed partial class WorldEvent
    {
        /// <summary>True for the champion stage of a chain (stage 2 or later, a kill_boss goal): world_events_chain_champion_* apply.</summary>
        internal bool IsChainChampionStage => (Request?.ChainStage ?? 1) > 1 && Composition?.Goal?.TypeKind == GoalType.KillBoss;

        /// <summary>The band of the last wave spawned (boss adds draw from it).</summary>
        private LevelBand? lastWaveBand;

        private double nextFinaleAddsAt, nextChampionAddsAt;
        private bool finaleEnrageAnnounced, championEnrageAnnounced;

        /// <summary>This run's roll against world_events_chain_chance, made once, the first time its chain decision is asked.</summary>
        private double? chainChanceRoll;

        /// <summary>
        /// Once a second: the finale boss's and the chained champion's adds (every add interval while it lives) and their
        /// enrage line (once). Nothing happens while the add counts and enrage timers are not set. Never throws.
        /// </summary>
        private void TickBossTuning(double now)
        {
            try
            {
                if (Composition?.Goal?.TypeKind == GoalType.Hold)
                {
                    var finale = Spawner.LiveWaveCreatures.FirstOrDefault(c => c != null && c.WorldEventFinaleBoss && !c.IsDead && !c.IsDestroyed);

                    if (finale != null)
                    {
                        TickBossAdds(finale, HoldKeys.FinaleAddCount, HoldKeys.FinaleAddIntervalSeconds, ref nextFinaleAddsAt, now);
                        TickEnrageLine(finale, HoldSettings.FinaleEnrageMult(finale, now), ref finaleEnrageAnnounced);
                    }
                }

                if (IsChainChampionStage)
                {
                    var champion = Spawner.BossCreature;

                    if (champion != null && !champion.IsDead && !champion.IsDestroyed)
                    {
                        TickBossAdds(champion, HoldKeys.ChampionAddCount, HoldKeys.ChampionAddIntervalSeconds, ref nextChampionAddsAt, now);
                        TickEnrageLine(champion, HoldSettings.ChampionEnrageMult(champion.WorldEventBossSpawnedAt, now), ref championEnrageAnnounced);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} boss tuning tick threw", ex);
            }
        }

        private void TickBossAdds(ACE.Server.WorldObjects.Creature boss, string countKey, string intervalKey, ref double nextAt, double now)
        {
            var count = HoldSettings.Integer(countKey, 0);

            if (count <= 0)
            {
                nextAt = 0;
                return;
            }

            var interval = Math.Max(5, HoldSettings.Number(intervalKey, 30));

            // The first adds come one interval after the boss arrives (or after the setting was turned on).
            if (nextAt <= 0)
            {
                nextAt = now + interval;
                return;
            }

            if (now < nextAt || boss.Location == null)
                return;

            nextAt = now + interval;

            // Never past the run's most alive at once.
            var maxAlive = Composition?.Goal?.Protect?.MaxAlive > 0 ? Composition.Goal.Protect.MaxAlive : Composition?.Source?.EffectiveMaxAlive ?? 0;

            if (maxAlive > 0)
                count = Math.Min(count, Math.Max(0, maxAlive - Spawner.LiveCount));

            var wcids = WorldEventHoldRules.PickAdds(Composition?.Roster?.Members, lastWaveBand, boss.WeenieClassId, count, holdWaveRng);

            if (wcids.Count == 0)
            {
                log.Info($"[WORLDEVENT] run={RunId} boss adds for {boss.Name}: none placed (field full or no member to draw)");
                return;
            }

            var band = lastWaveBand ?? new LevelBand(1, 275);
            var anchors = new List<ACE.Entity.Position> { new ACE.Entity.Position(boss.Location) };

            Spawner.SpawnWave(this, new WavePick(wcids, null), anchors, WorldEventSpawner.BossAddsWaveIndex, band, CrowdHealthMult * Pace.HealthMult);

            log.Info($"[WORLDEVENT] run={RunId} boss adds for {boss.Name}: {wcids.Count} ({string.Join(",", wcids)}); next in {interval:0} s");
            BroadcastHoldLine($"{boss.Name} calls for aid!");
        }

        private void TickEnrageLine(ACE.Server.WorldObjects.Creature boss, double mult, ref bool announced)
        {
            if (announced || !(mult > 1.0))
                return;

            announced = true;

            log.Info($"[WORLDEVENT] run={RunId} {boss.Name} enrages: damage x{mult:0.##}");
            BroadcastHoldLine($"{boss.Name} flies into a rage!");
        }

        /// <summary>A World Event line from the Hold settings' own mechanics (adds, enrage).</summary>
        private void BroadcastHoldLine(string text)
        {
            WorldEventAnnouncer.Broadcast(text);
        }
        /// <summary>
        /// world_events_hold_&lt;role&gt;_level_offset on a mixed wave: each slot of a role with an offset is redrawn from the
        /// roster's members in the wave's band moved by that offset (a ranged slot from the ranged members of its kind,
        /// caster or archer). A slot with no member there keeps its monster. Nothing happens while every offset is 0.
        /// </summary>
        private void ApplyRoleLevelOffsets(HoldWavePlan plan, WavePick pick, ISet<int> keep, int waveNumber)
        {
            if (plan?.Roles == null || pick == null)
                return;

            var offsets = new Dictionary<HoldDamageRole, int>();

            foreach (HoldDamageRole role in Enum.GetValues(typeof(HoldDamageRole)))
            {
                var offset = HoldSettings.RoleLevelOffset(role);

                if (offset != 0)
                    offsets[role] = offset;
            }

            if (offsets.Count == 0)
                return;

            var trash = plan.ReplacedTrash ?? pick.Trash;
            var members = Composition?.Roster?.Members ?? new List<ACE.Server.WorldEvents.Defs.FamilyMember>();

            HoldDamageRole RoleOf(int i)
            {
                if (i >= plan.Roles.Length)
                    return HoldDamageRole.None;

                var role = plan.Roles[i];

                return role switch
                {
                    HoldRole.Runner => HoldDamageRole.Runner,
                    HoldRole.Corrupted => HoldDamageRole.Corrupted,
                    HoldRole.Ranged => i < trash.Count && WorldEventRangedMembers.IsCasterWcid(trash[i]) ? HoldDamageRole.Caster : HoldDamageRole.Ranged,
                    _ => HoldDamageRole.Melee
                };
            }

            var pools = new Dictionary<HoldDamageRole, IReadOnlyList<uint>>();

            IReadOnlyList<uint> PoolFor(int i)
            {
                var role = RoleOf(i);

                if (!offsets.TryGetValue(role, out var offset))
                    return null;

                if (pools.TryGetValue(role, out var cached))
                    return cached;

                var band = WorldEventHoldRules.ShiftBand(pick.Band, offset);
                var inBand = members.Where(m => m != null && m.Wcid != 0 && m.Role == 0 && band.Contains(m.Level));

                if (role == HoldDamageRole.Ranged || role == HoldDamageRole.Caster)
                    inBand = inBand.Where(m => WorldEventRangedMembers.IsRanged(m) && WorldEventRangedMembers.IsCasterWcid(m.Wcid) == (role == HoldDamageRole.Caster));

                var pool = inBand.Select(m => m.Wcid).Distinct().ToList();
                pools[role] = pool;

                return pool;
            }

            var redrawn = WorldEventHoldRules.RedrawForLevelOffsets(trash, keep, i => offsets.TryGetValue(RoleOf(i), out var o) ? o : 0, PoolFor, holdWaveRng, out var moved);

            if (moved.Count == 0)
            {
                log.Info($"[WORLDEVENT] run={RunId} hold wave {waveNumber}: role level offsets [{string.Join(", ", offsets.Select(o => $"{o.Key}{o.Value:+0;-0}"))}] " +
                         $"found no member in the moved band (band {pick.Band}); the wave is unchanged");
                return;
            }

            plan.ReplacedTrash = redrawn;
            plan.LevelOffsetSlots = moved;

            log.Info($"[WORLDEVENT] run={RunId} hold wave {waveNumber}: {moved.Count} slot(s) redrawn for role level offsets " +
                     $"[{string.Join(", ", offsets.Select(o => $"{o.Key}{o.Value:+0;-0}"))}] from band {pick.Band}");
        }
    }
}
