using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// One running object-anchored wave encounter. Shaped after MlDigsiteEncounter: every mutable field is
    /// guarded by this object's own lock, because the death hook runs on a landblock thread while the tick
    /// runs on the world thread, and <see cref="MarkEnded"/> is a latch that returns true to exactly one
    /// caller.
    ///
    /// Two creature sets, deliberately separate:
    ///   - SPAWNED: everything this encounter ever put into the world, for the end-of-run cleanup. Closed at
    ///     the end so a spawn still in flight is refused rather than orphaned.
    ///   - ALIVE: the current wave's creatures still standing. A wave is cleared when this empties.
    /// </summary>
    public sealed class WaveEncounter
    {
        private readonly object sync = new object();

        private readonly Dictionary<uint, Creature> alive = new Dictionary<uint, Creature>();
        private readonly List<Creature> spawned = new List<Creature>();

        private bool spawnedClosed;
        private bool ended;
        private bool clearQuestAssigned;

        public WaveEncounter(uint encounterId, WaveAnchorKey key, WorldObject anchor, Position anchorPosition,
            int totalWaves, uint rosterBaseWcid, TimeSpan interWaveDelay, string clearQuest, string starterName, DateTime now)
        {
            EncounterId = encounterId;
            Key = key;
            Anchor = anchor;
            AnchorWcid = anchor?.WeenieClassId ?? 0;
            AnchorName = anchor?.Name;
            AnchorPosition = anchorPosition;
            TotalWaves = totalWaves;
            RosterBaseWcid = rosterBaseWcid;
            InterWaveDelay = interWaveDelay < TimeSpan.Zero ? TimeSpan.Zero : interWaveDelay;
            ClearQuest = string.IsNullOrWhiteSpace(clearQuest) ? null : clearQuest;
            StarterName = starterName;
            StartedUtc = now;
            LastPresenceUtc = now;
            nextWaveDue = now;
            Text = WaveEncounterText.For(AnchorWcid);
        }

        public uint EncounterId { get; }
        public WaveAnchorKey Key { get; }
        public WorldObject Anchor { get; }
        public uint AnchorWcid { get; }
        public string AnchorName { get; }
        public Position AnchorPosition { get; }
        public int TotalWaves { get; }
        public uint RosterBaseWcid { get; }
        public TimeSpan InterWaveDelay { get; }
        public string ClearQuest { get; }
        public string StarterName { get; }
        public DateTime StartedUtc { get; }
        public WaveEncounterText Text { get; }

        public int CurrentWave { get { lock (sync) return currentWave; } }
        private int currentWave;

        public DateTime? NextWaveDue { get { lock (sync) return nextWaveDue; } }
        private DateTime? nextWaveDue;

        public DateTime LastPresenceUtc { get { lock (sync) return lastPresence; } private set { lock (sync) lastPresence = value; } }
        private DateTime lastPresence;

        public int AliveCount { get { lock (sync) return alive.Count; } }

        public bool Ended { get { lock (sync) return ended; } }

        public WaveEndReason EndReason { get; private set; }

        public void MarkPresence(DateTime now) => LastPresenceUtc = now;

        /// <summary>Starts wave <paramref name="wave"/>: the live set is emptied and the due time cleared.</summary>
        public void BeginWave(int wave)
        {
            lock (sync)
            {
                currentWave = wave;
                nextWaveDue = null;
                alive.Clear();
            }
        }

        /// <summary>
        /// Registers a creature for cleanup BEFORE it enters the world. False once the encounter has ended:
        /// the caller must then destroy the creature itself, or it would stand in the world with TimeToRot -1
        /// and nothing left to remove it.
        /// </summary>
        public bool Adopt(Creature creature)
        {
            lock (sync)
            {
                if (spawnedClosed || ended)
                    return false;

                spawned.Add(creature);
                return true;
            }
        }

        /// <summary>Counts a creature that actually made it into the world toward the current wave.</summary>
        public void TrackAlive(Creature creature)
        {
            lock (sync)
            {
                if (!ended)
                    alive[creature.Guid.Full] = creature;
            }
        }

        /// <summary>
        /// Records one death (the death hook, landblock thread). Returns false when the creature is not in the
        /// live wave or the encounter has ended. When the death empties a non-final wave, arms the next wave
        /// <see cref="InterWaveDelay"/> from <paramref name="now"/>. When it leaves exactly one creature alive
        /// in the final wave, hands that creature back in <paramref name="clearQuestCarrier"/> (once per
        /// encounter) so the caller can give it the clear quest.
        /// </summary>
        public bool NoteDeath(uint guid, DateTime now, out Creature clearQuestCarrier)
        {
            clearQuestCarrier = null;

            lock (sync)
            {
                if (ended || !alive.Remove(guid))
                    return false;

                AfterRemovalLocked(now, out clearQuestCarrier);
                return true;
            }
        }

        /// <summary>
        /// The reap's sweep for creatures that left the world WITHOUT dying (a GM destroy, a decay pass that
        /// should never fire at TimeToRot -1, anything else). Nothing will ever report them, so the wave would
        /// otherwise hold open until the TTL. Returns how many were dropped.
        /// </summary>
        public int PruneGone(DateTime now, out Creature clearQuestCarrier)
        {
            clearQuestCarrier = null;

            lock (sync)
            {
                if (ended || alive.Count == 0)
                    return 0;

                var gone = alive.Where(kvp => kvp.Value == null || kvp.Value.IsDestroyed).Select(kvp => kvp.Key).ToList();

                foreach (var guid in gone)
                    alive.Remove(guid);

                if (gone.Count > 0)
                    AfterRemovalLocked(now, out clearQuestCarrier);

                return gone.Count;
            }
        }

        /// <summary>
        /// The clear-quest check for the moment a wave is placed (world thread), for a final wave that came up
        /// with only one creature in it. Once per encounter, same latch as the death path.
        /// </summary>
        public Creature TakeClearQuestCarrierAfterSpawn()
        {
            lock (sync)
                return TakeClearQuestCarrierLocked();
        }

        private void AfterRemovalLocked(DateTime now, out Creature clearQuestCarrier)
        {
            clearQuestCarrier = TakeClearQuestCarrierLocked();

            if (alive.Count == 0 && currentWave < TotalWaves && nextWaveDue == null)
                nextWaveDue = now + InterWaveDelay;
        }

        private Creature TakeClearQuestCarrierLocked()
        {
            if (clearQuestAssigned || ended)
                return null;

            if (!WaveEncounterRules.ShouldAssignClearQuest(currentWave, TotalWaves, alive.Count, ClearQuest != null))
                return null;

            clearQuestAssigned = true;
            return alive.Values.First();
        }

        /// <summary>The end latch. True to exactly one caller; closes the spawned list at the same moment.</summary>
        public bool MarkEnded(WaveEndReason reason)
        {
            lock (sync)
            {
                if (ended)
                    return false;

                ended = true;
                spawnedClosed = true;
                EndReason = reason;
                return true;
            }
        }

        public List<Creature> SpawnedSnapshot()
        {
            lock (sync)
                return spawned.ToList();
        }

        public override string ToString() => $"wave encounter {EncounterId} ({AnchorName} 0x{Key.AnchorGuid:X8} instance {Key.Instance})";
    }

    /// <summary>One anchor in one landblock instance: the registry and cooldown key.</summary>
    public readonly struct WaveAnchorKey : IEquatable<WaveAnchorKey>
    {
        public WaveAnchorKey(uint anchorGuid, uint instance)
        {
            AnchorGuid = anchorGuid;
            Instance = instance;
        }

        public uint AnchorGuid { get; }
        public uint Instance { get; }

        public bool Equals(WaveAnchorKey other) => AnchorGuid == other.AnchorGuid && Instance == other.Instance;
        public override bool Equals(object obj) => obj is WaveAnchorKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(AnchorGuid, Instance);
    }
}
