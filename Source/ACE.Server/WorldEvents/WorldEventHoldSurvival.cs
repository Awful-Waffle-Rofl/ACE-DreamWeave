using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Hold: the ways players keep the targets alive, each a goals.json setting and all off by default.
    ///   * corruptedKillHeal: a corrupted monster's death heals every standing target, with a target's line;
    ///   * shrine: a restoration shrine beside each target heals it when a player uses it, then rests;
    ///   * rescueHeal: killing a monster that was attacking a target heals that target a little (capped per wave);
    ///   * clearRegenPerSecond: a target with no hostile within clearRadius regenerates.
    /// Every one of them goes through Creature.ApplyWorldEventProtectedHeal, so the after-hit reduction and the
    /// per-wave healing budget (healBudgetPerWave) hold for all of them together.
    /// </summary>
    public sealed partial class WorldEvent
    {
        /// <summary>
        /// Queues a heal of <paramref name="share"/> of max health on <paramref name="target"/>'s own landblock and
        /// calls <paramref name="after"/> there with what it came to. Any thread.
        /// </summary>
        private void QueueProtectedHeal(Creature target, double share, bool isRescue, string why,
            Action<int, WorldEventHoldRules.ProtectedHealOutcome> after = null)
        {
            var landblock = target?.CurrentLandblock;

            if (landblock == null || !(share > 0))
                return;

            var t = target;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (t.IsDead || t.IsDestroyed)
                        return;

                    var healed = t.ApplyWorldEventProtectedHeal(share, isRescue, ACE.Common.Time.GetUnixTime(), out var outcome);

                    if (healed > 0)
                        log.Debug($"[WORLDEVENT] run={RunId} protected heal {why} {t.Name} +{healed} ({t.Health.Current}/{t.Health.MaxValue}, " +
                                  $"spent this wave {t.WorldEventHealSpentThisWave})");

                    after?.Invoke(healed, outcome);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={RunId} protected heal ({why}) threw", ex);
                }
            }));
        }

        /// <summary>
        /// A wave monster of a Hold run died (from OnCreatureDied, on its landblock thread): a corrupted one heals
        /// every standing target (protect.corruptedKillHeal) and one of them says so; and a monster that was
        /// attacking a target, killed by a player or a pet, restores a little of that target (protect.rescueHeal).
        /// </summary>
        private void OnHoldMonsterDied(Creature creature, DamageHistoryInfo lastDamager)
        {
            var protect = Composition?.Goal?.TypeKind == GoalType.Hold ? Composition?.Goal?.Protect : null;

            if (protect == null || creature == null || creature.WorldEventProtected)
                return;

            if (creature.WorldEventCorrupted && protect.CorruptedKillHeal > 0)
            {
                var targets = Spawner.LiveProtectedCreatures.Where(t => t != null && !t.IsDead).ToList();

                foreach (var target in targets)
                    QueueProtectedHeal(target, protect.CorruptedKillHeal, false, "corrupted kill");

                log.Info($"[WORLDEVENT] run={RunId} corrupted {creature.Name} slain; {targets.Count} target(s) heal {protect.CorruptedKillHeal:P0}");

                if (targets.Count > 0)
                {
                    Creature speaker;

                    lock (protectedLineRngSync)
                        speaker = targets[protectedLineRng.Next(targets.Count)];

                    SpeakLocally(speaker, ComposeProtectedLine(protect.Lines?.CorruptedKill, null), viaQueue: true);
                }
            }

            if (protect.RescueHeal > 0 && creature.AttackTarget is Creature victim && victim.WorldEventProtected
                && ReferenceEquals(victim.P_WorldEvent, this) && !victim.IsDead && WorldEventHoldRules.IsRescueKill(lastDamager?.IsPlayer ?? false,
                    lastDamager?.TryGetPetOwner() != null))
            {
                QueueProtectedHeal(victim, protect.RescueHeal, true, "rescue");
            }
        }

        // ---- clear regeneration --------------------------------------------------------------------------------

        /// <summary>
        /// Once a second: every standing target with no living wave monster within protect.clearRadius regenerates
        /// protect.clearRegenPerSecond. It stops the moment one comes within range. Never throws.
        /// </summary>
        private void TickHoldClearRegen()
        {
            try
            {
                var protect = Composition?.Goal?.TypeKind == GoalType.Hold ? Composition?.Goal?.Protect : null;

                if (protect == null || !(protect.ClearRegenPerSecond > 0))
                    return;

                var monsters = Spawner.LiveWaveCreatures.Where(m => m != null && !m.IsDead && m.Location != null)
                    .Select(m => m.Location).ToList();

                foreach (var target in Spawner.LiveProtectedCreatures)
                {
                    if (target == null || target.IsDead || target.Location == null)
                        continue;

                    if (target.Health.Current >= target.Health.MaxValue)
                        continue;

                    var nearest = monsters.Count == 0 ? (double?)null : monsters.Min(m => (double)target.Location.DistanceTo(m));

                    if (!WorldEventHoldRules.IsClear(nearest, protect.ClearRadius))
                        continue;

                    QueueProtectedHeal(target, Math.Min(0.1, protect.ClearRegenPerSecond), false, "clear regen");
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} clear regeneration threw", ex);
            }
        }

        // ---- restoration shrines -------------------------------------------------------------------------------

        private readonly object shrineSync = new object();

        /// <summary>Per shrine guid: when it is ready again (unix time). Absent = ready.</summary>
        private readonly Dictionary<uint, double> shrineReadyAt = new Dictionary<uint, double>();

        /// <summary>Per shrine guid: when its ready effect last played.</summary>
        private readonly Dictionary<uint, double> shrineReadyPulseAt = new Dictionary<uint, double>();

        /// <summary>How often a ready shrine replays its ready effect, in seconds.</summary>
        internal const double ShrineReadyPulseSeconds = 8.0;

        /// <summary>A shrine farther than this from every target restores nobody.</summary>
        internal const double ShrineBindMetres = 8.0;

        /// <summary>True when <paramref name="item"/> is one of this run's restoration shrines.</summary>
        internal bool IsHoldShrine(WorldObject item)
        {
            var shrine = Composition?.Goal?.TypeKind == GoalType.Hold ? Composition?.Goal?.Protect?.Shrine : null;

            return shrine != null && item != null && item.WeenieClassId == shrine.Wcid && Spawner.IsDecorGuid(item.Guid.Full);
        }

        /// <summary>
        /// A player used <paramref name="item"/> (Player.TryUseItem, on the player's landblock). When it is one of this
        /// run's shrines: on cooldown, the player is told how long is left; ready, the nearest standing target is
        /// restored by protect.shrineHeal (through the healing budget), the shrine rests for
        /// protect.shrineCooldownSeconds, the target shows the used effect and says a line. A heal that comes to
        /// nothing (full health, or the budget spent) gives the use back and says why. Returns true when it was a
        /// shrine (handled here, whatever happened), false for anything else.
        /// </summary>
        public bool TryUseHoldShrine(Player player, WorldObject item)
        {
            if (player == null || !IsHoldShrine(item))
                return false;

            var protect = Composition.Goal.Protect;
            var now = ACE.Common.Time.GetUnixTime();

            if (State != WorldEventState.Active)
            {
                Tell(player, "The shrine is quiet.");
                return true;
            }

            double readyAt;

            lock (shrineSync)
                shrineReadyAt.TryGetValue(item.Guid.Full, out readyAt);

            if (now < readyAt)
            {
                Tell(player, WorldEventHoldRules.ShrineCooldownLine(readyAt - now));
                return true;
            }

            var target = NearestProtected(item, ShrineBindMetres);

            if (target == null)
            {
                Tell(player, "There is no one here for the shrine to restore.");
                return true;
            }

            var cooldown = Math.Clamp(protect.ShrineCooldownSeconds, 1, 600);

            lock (shrineSync)
                shrineReadyAt[item.Guid.Full] = now + cooldown;

            var shrineGuid = item.Guid.Full;

            QueueProtectedHeal(target, protect.ShrineHeal, false, "shrine", (healed, outcome) =>
            {
                if (healed > 0)
                {
                    PlayScriptOn(target, protect.Shrine.UsedEffect);
                    SpeakLocally(target, ComposeProtectedLine(protect.Lines?.Shrine, null), viaQueue: false);

                    log.Info($"[WORLDEVENT] run={RunId} shrine used by {player.Name}: {target.Name} +{healed}; ready again in {cooldown:0} s");
                    return;
                }

                // Nothing restored: give the use back.
                lock (shrineSync)
                    shrineReadyAt.Remove(shrineGuid);

                Tell(player, WorldEventHoldRules.ShrineWastedLine(target.Name, outcome));
            });

            return true;
        }

        /// <summary>
        /// Once a second: a ready shrine replays its ready effect every <see cref="ShrineReadyPulseSeconds"/>, and plays
        /// it the moment its cooldown ends. Never throws.
        /// </summary>
        private void TickHoldShrines()
        {
            try
            {
                var shrine = Composition?.Goal?.TypeKind == GoalType.Hold ? Composition?.Goal?.Protect?.Shrine : null;

                if (shrine == null || !WorldEventHoldRules.TryParseBossEffect(shrine.ReadyEffect, out var ready) || ready == PlayScript.Invalid)
                    return;

                var now = ACE.Common.Time.GetUnixTime();

                foreach (var item in Spawner.LiveDecorObjects)
                {
                    if (item == null || item.WeenieClassId != shrine.Wcid)
                        continue;

                    bool due;

                    lock (shrineSync)
                    {
                        shrineReadyAt.TryGetValue(item.Guid.Full, out var readyAt);
                        shrineReadyPulseAt.TryGetValue(item.Guid.Full, out var pulsedAt);

                        due = WorldEventHoldRules.ShrineReadyPulseDue(now, readyAt, pulsedAt, ShrineReadyPulseSeconds);

                        if (due)
                            shrineReadyPulseAt[item.Guid.Full] = now;
                    }

                    if (due)
                        PlayScriptOn(item, shrine.ReadyEffect);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={RunId} shrine tick threw", ex);
            }
        }

        /// <summary>The nearest standing target of this run within <paramref name="metres"/> of <paramref name="wo"/>, or null.</summary>
        private Creature NearestProtected(WorldObject wo, double metres)
        {
            if (wo?.Location == null)
                return null;

            Creature best = null;
            var bestDist = double.MaxValue;

            foreach (var target in Spawner.LiveProtectedCreatures)
            {
                if (target == null || target.IsDead || target.Location == null)
                    continue;

                var d = wo.Location.DistanceTo(target.Location);

                if (d <= metres && d < bestDist)
                {
                    best = target;
                    bestDist = d;
                }
            }

            return best;
        }

        /// <summary>Plays a PlayScript (by name; "none" or unknown does nothing) on <paramref name="wo"/> through its landblock. Any thread.</summary>
        private void PlayScriptOn(WorldObject wo, string effect)
        {
            if (wo?.CurrentLandblock == null || !WorldEventHoldRules.TryParseBossEffect(effect, out var script) || script == PlayScript.Invalid)
                return;

            var target = wo;

            wo.CurrentLandblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                if (!target.IsDestroyed)
                    target.EnqueueBroadcast(new GameMessageScript(target.Guid, script, 1.0f));
            }));
        }

        private static void Tell(Player player, string text)
        {
            player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
