using System.Collections.Immutable;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// The players (by guid) whose side - the player or one of their pets - has damaged this creature, each with the
        /// Timers.RunningTime of that side's latest hit. Kept only for a creature that never attacks
        /// (PetEngagement.NeverAttacks); null until the first such hit. Read by PetEngagement.IsEngagedWithOwner from a
        /// pet's tick, possibly on another landblock group's thread, so the map is immutable and replaced whole: a reader
        /// takes one reference and sees an old map or a new one, never a half-written one. No lock.
        /// </summary>
        private ImmutableDictionary<uint, double> damagedByOwners;

        /// <summary>The current owner-damage map (see damagedByOwners); empty when nothing was recorded.</summary>
        public ImmutableDictionary<uint, double> DamagedByOwners => System.Threading.Volatile.Read(ref damagedByOwners) ?? ImmutableDictionary<uint, double>.Empty;

        /// <summary>
        /// Whether the player with guid <paramref name="ownerGuid"/>, or a pet of theirs, last damaged this creature no more
        /// than <paramref name="window"/> seconds before <paramref name="now"/>. One reference read and a dictionary lookup on
        /// an immutable map: false at once for a creature with no record (every normal monster).
        /// </summary>
        public bool WasDamagedByOwner(uint ownerGuid, double now, double window)
        {
            var map = System.Threading.Volatile.Read(ref damagedByOwners);

            return map != null && map.TryGetValue(ownerGuid, out var at) && now - at <= window;
        }

        /// <summary>
        /// Records (or refreshes) <paramref name="ownerGuid"/>'s latest hit at <paramref name="now"/>. Lock-free
        /// copy-on-write (ImmutableInterlocked), so two hits landing from two threads both stay recorded; a later time is
        /// never overwritten by an earlier one. Callers: PetEngagement.RecordDamage only.
        /// </summary>
        internal void RecordDamageByOwner(uint ownerGuid, double now)
        {
            ImmutableInterlocked.Update(ref damagedByOwners, map =>
            {
                map ??= ImmutableDictionary<uint, double>.Empty;

                return map.TryGetValue(ownerGuid, out var at) && at >= now ? map : map.SetItem(ownerGuid, now);
            });
        }
    }
}
