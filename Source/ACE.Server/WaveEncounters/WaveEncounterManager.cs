using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// The object-anchored wave runner: one use of an anchor (PropertyBool WaveEncounterAnchor, e.g. the D6
    /// Sounding Drum) starts an encounter of N waves read from roster weenies (base + wave - 1, the Proving
    /// Grounds roster format); the next wave spawns InterWaveDelay after the previous one is fully dead.
    ///
    /// THE ANCHOR CARRIES ITS CONFIG, reusing the Proving Grounds ids: int 9029 WaveChallengeWaves (wave
    /// count), int 9031 WaveChallengeRosterBaseWcid (wave 1's roster), float 9004
    /// WaveChallengeInterWaveDelay (seconds), and string 9021 WaveEncounterClearQuest.
    ///
    /// THREADING, mirroring MlDigsiteManager:
    ///   - TryStart runs on the anchor's landblock thread (WorldObject.OnActivate). It only admits and
    ///     registers - it spawns nothing. Admission is serialised under startLock.
    ///   - OnEncounterCreatureDied runs on the landblock thread that killed the creature. It only RECORDS
    ///     (under the encounter's own lock) and arms the next wave's due time. The one thing it also does is
    ///     hand the clear quest to the final wave's last creature, because that must be on the survivor
    ///     before ITS OnDeath reads KillQuest - a tick later could be too late if both die in one pass.
    ///   - Tick runs on the world thread AFTER LandblockManager.Tick (WorldManager.UpdateGameWorld), so no
    ///     landblock work is in flight. EVERY spawn, end and cleanup happens here.
    ///
    /// LIFECYCLE (owner rulings 2026-09-24): win = the final wave cleared. Wipe or walk-away = no living
    /// player within the presence radius for the grace period. TTL = failure. Landblock unload or the anchor
    /// destroyed = end (unload has already removed every creature). Every end starts the anchor's cooldown,
    /// and every failure destroys what is left WITHOUT Die() - no loot, XP or credit. A restart loses the
    /// registry and the cooldowns; the creatures never persisted (IsDynamicThatShouldPersistToShard, with
    /// WaveEncounterOrphanFilter as the backstop).
    /// </summary>
    public static class WaveEncounterManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<WaveAnchorKey, WaveEncounter> encounters =
            new ConcurrentDictionary<WaveAnchorKey, WaveEncounter>();

        /// <summary>When each anchor may start again. Written at every end, read at every start.</summary>
        private static readonly ConcurrentDictionary<WaveAnchorKey, DateTime> cooldownUntil =
            new ConcurrentDictionary<WaveAnchorKey, DateTime>();

        private static readonly object startLock = new object();

        private static int nextEncounterId;

        private static DateTime nextReap = DateTime.MinValue;
        private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(15);

        /// <summary>Fallback breather when an anchor carries no float 9004.</summary>
        public const double DefaultInterWaveDelaySeconds = 1.0;

        public static IReadOnlyList<WaveEncounter> LiveEncounters => encounters.Values.Where(e => !e.Ended).ToList();

        // ---- start ----------------------------------------------------------------------------------------

        /// <summary>
        /// One use of an anchor. Refuses (with the anchor's own line, to <paramref name="player"/> only) while
        /// an encounter is running on it or its cooldown is live; otherwise registers a new encounter whose
        /// wave 1 is due now, and announces the start. The first wave is placed by the next heartbeat's tick.
        /// </summary>
        public static bool TryStart(WorldObject anchor, Player player)
        {
            if (anchor?.Location == null || player == null)
                return false;

            var totalWaves = anchor.GetProperty(PropertyInt.WaveChallengeWaves) ?? 0;
            var rosterBase = (uint)(anchor.GetProperty(PropertyInt.WaveChallengeRosterBaseWcid) ?? 0);

            if (totalWaves < 1 || rosterBase == 0)
            {
                log.Error($"[WAVE_ENCOUNTER] anchor {anchor.Name} (0x{anchor.Guid.Full:X8}, wcid {anchor.WeenieClassId}) is flagged WaveEncounterAnchor but has WaveChallengeWaves {totalWaves} / WaveChallengeRosterBaseWcid {rosterBase}; nothing started");
                return false;
            }

            var delaySeconds = anchor.GetProperty(PropertyFloat.WaveChallengeInterWaveDelay) ?? DefaultInterWaveDelaySeconds;

            if (double.IsNaN(delaySeconds) || delaySeconds < 0)
                delaySeconds = 0;

            var clearQuest = anchor.GetProperty(PropertyString.WaveEncounterClearQuest);
            var key = new WaveAnchorKey(anchor.Guid.Full, anchor.Location.Instance);
            var text = WaveEncounterText.For(anchor.WeenieClassId);
            var now = DateTime.UtcNow;

            WaveEncounter encounter;

            lock (startLock)
            {
                // Registered at all counts as running, even if its end latch has just closed: Finish removes it
                // under this same lock, after writing the cooldown, so there is no gap to start into.
                var running = encounters.ContainsKey(key);
                DateTime? until = cooldownUntil.TryGetValue(key, out var u) ? u : (DateTime?)null;

                var refusal = WaveEncounterRules.CheckStart(running, until, now, out var remaining);

                if (refusal == WaveStartRefusal.Running)
                {
                    Tell(player, text.RefuseRunning);
                    return false;
                }

                if (refusal == WaveStartRefusal.Cooldown)
                {
                    Tell(player, WaveEncounterText.Format(text.RefuseCooldown, minutes: WaveEncounterRules.CooldownMinutes(remaining)));
                    return false;
                }

                var id = (uint)Interlocked.Increment(ref nextEncounterId);

                encounter = new WaveEncounter(id, key, anchor, new Position(anchor.Location), totalWaves, rosterBase,
                    TimeSpan.FromSeconds(delaySeconds), clearQuest, player.Name, now);

                encounters[key] = encounter;
                cooldownUntil.TryRemove(key, out _);
            }

            log.Info($"[WAVE_ENCOUNTER] started {encounter} by {player.Name}: {totalWaves} waves from roster {rosterBase}, breather {delaySeconds:0.##}s, clear quest {clearQuest ?? "(none)"}");

            Announce(encounter, WaveEncounterText.Format(text.Start, total: totalWaves), ChatMessageType.WorldBroadcast);

            // The starter always hears the start line, even if the presence scan somehow missed them.
            if (!player.IsDead && (player.Location == null || player.Location.Instance != key.Instance || player.Location.DistanceTo(encounter.AnchorPosition) > WaveEncounterTunables.PresenceRadius))
                Tell(player, WaveEncounterText.Format(text.Start, total: totalWaves), ChatMessageType.WorldBroadcast);

            return true;
        }

        // ---- the death hook -------------------------------------------------------------------------------

        /// <summary>
        /// Creature.Die's report (landblock thread, two-key gated at the call site). Records only; see the
        /// class remarks for the one exception, the clear quest hand-off.
        /// </summary>
        public static void OnEncounterCreatureDied(Creature creature)
        {
            var encounter = creature?.P_WaveEncounter;

            if (encounter == null)
                return;

            if (!encounters.TryGetValue(encounter.Key, out var live) || !ReferenceEquals(live, encounter))
                return;

            if (!encounter.NoteDeath(creature.Guid.Full, DateTime.UtcNow, out var carrier))
                return;

            AssignClearQuest(encounter, carrier);
        }

        /// <summary>
        /// Gives the final wave's last creature the anchor's clear quest as its KillQuest (PropertyString 45),
        /// which Creature.OnDeath reads at death time - so its killer is credited by the ordinary kill-task
        /// path (for a Bluespire rung name, including the ladder's own row bootstrap and cooldown line).
        /// </summary>
        private static void AssignClearQuest(WaveEncounter encounter, Creature carrier)
        {
            if (carrier == null || encounter.ClearQuest == null)
                return;

            if (carrier.IsDead || carrier.IsDestroyed)
            {
                log.Warn($"[WAVE_ENCOUNTER] {encounter} last creature {carrier.Name} (0x{carrier.Guid.Full:X8}) was already dead or gone; clear quest {encounter.ClearQuest} not assigned");
                return;
            }

            carrier.KillQuest = encounter.ClearQuest;

            log.Info($"[WAVE_ENCOUNTER] {encounter} last creature standing {carrier.Name} (0x{carrier.Guid.Full:X8}) now carries KillQuest {encounter.ClearQuest}");
        }

        // ---- the tick -------------------------------------------------------------------------------------

        /// <summary>
        /// World thread, every heartbeat. Returns at once when nothing is live. The wave-due check runs every
        /// call so a 1 s breather is 1 s; the reap (presence, TTL, landblock, anchor) runs every 15 s.
        /// </summary>
        public static void Tick()
        {
            if (encounters.IsEmpty)
                return;

            var now = DateTime.UtcNow;
            var reap = now >= nextReap;

            if (reap)
                nextReap = now + ReapInterval;

            foreach (var encounter in encounters.Values.ToList())
            {
                try
                {
                    if (encounter.Ended)
                    {
                        Remove(encounter);
                        continue;
                    }

                    if (reap && ReapOne(encounter, now))
                        continue;

                    Drive(encounter, now);
                }
                catch (Exception ex)
                {
                    log.Error($"[WAVE_ENCOUNTER] {encounter} tick threw", ex);
                }
            }
        }

        /// <summary>The 15 s reap for one encounter. True when it ended the encounter.</summary>
        private static bool ReapOne(WaveEncounter encounter, DateTime now)
        {
            var anchor = encounter.AnchorPosition;

            var landblockLoaded = LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance);
            var anchorAlive = encounter.Anchor != null && !encounter.Anchor.IsDestroyed;

            if (landblockLoaded && MlDigsiteAudience.AnyPresent(anchor, WaveEncounterTunables.PresenceRadius))
                encounter.MarkPresence(now);

            if (landblockLoaded && encounter.PruneGone(now, out var carrier) > 0)
            {
                log.Warn($"[WAVE_ENCOUNTER] {encounter} dropped creature(s) that left the world without dying");
                AssignClearQuest(encounter, carrier);
            }

            if (!WaveEncounterRules.ShouldEnd(landblockLoaded, anchorAlive, now - encounter.StartedUtc, WaveEncounterTunables.Ttl,
                    now - encounter.LastPresenceUtc, WaveEncounterTunables.WipeGrace, out var reason))
            {
                return false;
            }

            Finish(encounter, reason);
            return true;
        }

        private static void Drive(WaveEncounter encounter, DateTime now)
        {
            var action = WaveEncounterRules.Decide(encounter.CurrentWave, encounter.TotalWaves, encounter.AliveCount, encounter.NextWaveDue, now);

            switch (action)
            {
                case WaveTickAction.Win:
                    Finish(encounter, WaveEndReason.Win);
                    break;

                case WaveTickAction.SpawnWave:
                    SpawnWave(encounter, encounter.CurrentWave + 1);
                    break;
            }
        }

        /// <summary>
        /// Places wave <paramref name="wave"/> from its roster weenie. Fails CLOSED like the Proving Grounds:
        /// a missing roster, or a wave that puts nothing into the world, ends the encounter rather than
        /// leaving an empty gallery that can never advance.
        /// </summary>
        private static void SpawnWave(WaveEncounter encounter, int wave)
        {
            var anchor = encounter.AnchorPosition;

            // Never spawn into an unloaded landblock: LandblockManager.AddObject would LOAD it again.
            if (!LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance))
            {
                Finish(encounter, WaveEndReason.LandblockUnloaded);
                return;
            }

            if (encounter.Anchor == null || encounter.Anchor.IsDestroyed)
            {
                Finish(encounter, WaveEndReason.AnchorGone);
                return;
            }

            var rosterWcid = WaveRoster.RosterWcid(encounter.RosterBaseWcid, wave);
            var roster = DatabaseManager.World.GetCachedWeenie(rosterWcid);

            if (roster == null)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave}: roster weenie {rosterWcid} does not exist; ending the encounter");
                Finish(encounter, WaveEndReason.SpawnFailed);
                return;
            }

            var declaredIndex = roster.GetProperty(PropertyInt.WaveChallengeWaveIndex);

            if (declaredIndex != null && declaredIndex.Value != wave)
                log.Error($"[WAVE_ENCOUNTER] {encounter} roster weenie {rosterWcid} declares WaveChallengeWaveIndex {declaredIndex.Value} but is being spawned as wave {wave}; the roster wcid block is misaligned");

            var rejections = new List<string>();
            var entries = WaveRoster.Read(roster.PropertiesGenerator, anchor.LandblockId.Landblock, rejections);

            foreach (var rejection in rejections)
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave} roster {rosterWcid}: {rejection}; skipped");

            encounter.BeginWave(wave);

            var placed = 0;

            foreach (var entry in entries)
            {
                if (WaveEncounterSpawner.TrySpawn(encounter, entry, wave) != null)
                    placed++;
            }

            if (placed == 0)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} wave {wave} roster {rosterWcid} put nothing into the world; ending the encounter");
                Finish(encounter, WaveEndReason.SpawnFailed);
                return;
            }

            if (placed < entries.Count)
                log.Warn($"[WAVE_ENCOUNTER] {encounter} wave {wave}: placed {placed} of {entries.Count}");

            log.Info($"[WAVE_ENCOUNTER] {encounter} wave {wave} of {encounter.TotalWaves}: {placed} creature(s) placed");

            var text = encounter.Text;

            Announce(encounter, WaveEncounterText.Format(text.Wave, wave, encounter.TotalWaves), ChatMessageType.WorldBroadcast);

            if (wave == encounter.TotalWaves && !string.IsNullOrEmpty(text.FinalWave))
                Announce(encounter, WaveEncounterText.Format(text.FinalWave, wave, encounter.TotalWaves), ChatMessageType.WorldBroadcast);

            // A final wave that came up with a single creature (the other failed to place) hands it the
            // clear quest now; the ordinary case is handed off by the death hook.
            AssignClearQuest(encounter, encounter.TakeClearQuestCarrierAfterSpawn());
        }

        // ---- the end --------------------------------------------------------------------------------------

        /// <summary>
        /// THE one way an encounter ends (world thread; the admin stop reaches it through
        /// <see cref="RequestStop"/>). Latch first, then the registry and the cooldown, then the line, and
        /// the cleanup last in a finally so a throw can never strand TimeToRot -1 creatures in the world.
        /// </summary>
        private static void Finish(WaveEncounter encounter, WaveEndReason reason)
        {
            if (encounter == null || !encounter.MarkEnded(reason))
                return;

            try
            {
                var cooldown = WaveEncounterTunables.Cooldown;

                // Under the admission lock, cooldown BEFORE the registry removal: a use landing between the
                // two would otherwise see neither a live encounter nor a cooldown and start a fresh one.
                lock (startLock)
                {
                    if (cooldown > TimeSpan.Zero)
                        cooldownUntil[encounter.Key] = DateTime.UtcNow + cooldown;

                    Remove(encounter);
                }

                log.Info($"[WAVE_ENCOUNTER] ended {encounter} reason={reason} at wave {encounter.CurrentWave} of {encounter.TotalWaves}, alive={encounter.AliveCount}, cooldown={cooldown.TotalSeconds:0}s");

                var anchor = encounter.AnchorPosition;

                if (LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance))
                {
                    var line = WaveEncounterRules.IsWin(reason) ? encounter.Text.Win : encounter.Text.Fail;
                    Announce(encounter, WaveEncounterText.Format(line, encounter.CurrentWave, encounter.TotalWaves));
                }
            }
            catch (Exception ex)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} end bookkeeping threw", ex);
            }
            finally
            {
                DestroyRemaining(encounter);
            }
        }

        private static void Remove(WaveEncounter encounter)
        {
            if (encounters.TryGetValue(encounter.Key, out var live) && ReferenceEquals(live, encounter))
                encounters.TryRemove(encounter.Key, out _);
        }

        /// <summary>
        /// Takes every creature still standing out of the world WITHOUT Die() - no corpse, no loot, no XP,
        /// no kill credit. The back-reference is nulled on every creature first, so nothing can report into a
        /// finished encounter. A dead or dying creature is left to its own death chain (its corpse was earned).
        /// Each destroy is queued on the creature's own landblock, as MlDigsiteManager.DestroyHeld does; after
        /// a landblock unload every creature is already destroyed and this is a no-op.
        /// </summary>
        private static void DestroyRemaining(WaveEncounter encounter)
        {
            foreach (var creature in encounter.SpawnedSnapshot())
            {
                try
                {
                    if (creature == null)
                        continue;

                    creature.P_WaveEncounter = null;

                    if (creature.IsDead || creature.IsDestroyed)
                        continue;

                    var landblock = creature.CurrentLandblock;
                    var target = creature;

                    if (landblock != null)
                        landblock.EnqueueAction(new ActionEventDelegate(() =>
                        {
                            if (!target.IsDestroyed)
                                target.Destroy();
                        }));
                    else
                        target.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[WAVE_ENCOUNTER] {encounter} cleanup threw for 0x{creature?.Guid.Full:X8}", ex);
                }
            }
        }

        // ---- admin ----------------------------------------------------------------------------------------

        /// <summary>Stops every live encounter (the admin command). Runs the ordinary end path.</summary>
        public static int StopAll()
        {
            var stopped = 0;

            foreach (var encounter in encounters.Values.ToList())
            {
                if (encounter.Ended)
                    continue;

                RequestStop(encounter);
                stopped++;
            }

            return stopped;
        }

        /// <summary>
        /// Queues the end onto the world thread (the next heartbeat), so an admin command issued on a network
        /// thread never destroys creatures under a running landblock pass.
        /// </summary>
        private static void RequestStop(WaveEncounter encounter)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() => Finish(encounter, WaveEndReason.Stopped)));
        }

        /// <summary>Clears every anchor cooldown (the admin command).</summary>
        public static int ClearCooldowns()
        {
            var count = cooldownUntil.Count;
            cooldownUntil.Clear();
            return count;
        }

        public static IEnumerable<string> StatusLines()
        {
            var now = DateTime.UtcNow;
            var any = false;

            foreach (var e in encounters.Values.ToList())
            {
                any = true;
                var due = e.NextWaveDue;
                var dueText = due == null ? "-" : $"{Math.Max(0, (due.Value - now).TotalSeconds):0.0}s";
                yield return $"{e}: wave {e.CurrentWave}/{e.TotalWaves}, alive {e.AliveCount}, next wave in {dueText}, running {(now - e.StartedUtc).TotalMinutes:0.0} min, started by {e.StarterName}";
            }

            foreach (var kvp in cooldownUntil.ToList())
            {
                if (kvp.Value <= now)
                    continue;

                any = true;
                yield return $"cooldown: anchor 0x{kvp.Key.AnchorGuid:X8} instance {kvp.Key.Instance}, {(kvp.Value - now).TotalSeconds:0}s left";
            }

            if (!any)
                yield return "No wave encounters are running and no anchor is on cooldown.";
        }

        // ---- chat -----------------------------------------------------------------------------------------

        /// <summary>
        /// One line to every living player within the presence radius of the anchor, same landblock instance -
        /// the same scan (MlDigsiteAudience) that decides whether anyone is still there. Never throws.
        /// </summary>
        private static void Announce(WaveEncounter encounter, string message, ChatMessageType type = ChatMessageType.Broadcast)
        {
            if (encounter?.AnchorPosition == null || string.IsNullOrEmpty(message))
                return;

            try
            {
                foreach (var player in MlDigsiteAudience.Participants(encounter.AnchorPosition, WaveEncounterTunables.PresenceRadius))
                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, type));
            }
            catch (Exception ex)
            {
                log.Error($"[WAVE_ENCOUNTER] {encounter} announcement threw", ex);
            }
        }

        private static void Tell(Player player, string message, ChatMessageType type = ChatMessageType.Broadcast)
        {
            if (player?.Session == null || string.IsNullOrEmpty(message))
                return;

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, type));
        }
    }
}
