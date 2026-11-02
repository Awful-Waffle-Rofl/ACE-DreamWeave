using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>
    /// A guid this client was sent a CreateObject for, with the ObjectInstance sequence that create carried, so a
    /// DeleteObject for it can be rebuilt byte for byte without the object.
    /// </summary>
    public readonly struct LedgerObject
    {
        public readonly uint Guid;
        public readonly byte[] InstanceSequence;

        public LedgerObject(uint guid, byte[] instanceSequence)
        {
            Guid = guid;
            InstanceSequence = instanceSequence;
        }
    }

    /// <summary>
    /// Which copy of each landblock one client currently holds objects from (teleport_flush_scope 2).
    ///
    /// A landblock's per-realm instance copies share 32-bit object guids and client cell ids, so the client can
    /// hold at most one copy of a landblock id at a time; the ledger mirrors that with at most one entry per
    /// landblock id. Fed from the two NetworkSession.EnqueueSend loops (ClientObjectBook): a CreateObject files
    /// its guid under the landblock the object stood in, a DeleteObject or PickupEvent unfiles it. Recording into
    /// a landblock held under a different instance replaces that entry, because the client now holds the newer
    /// copy. Objects stay filed where they were created; the 3x3 neighbourhood a teleport checks covers one
    /// landblock of drift.
    ///
    /// Pure bookkeeping: no WorldObject references, no locking (ClientObjectBook.SyncRoot guards it), no sends.
    /// Bounded at <see cref="DefaultCapacity"/> guids by evicting the least recently recorded whole landblock,
    /// never the one being recorded into.
    /// </summary>
    public sealed class ClientObjectLedger
    {
        public const int DefaultCapacity = 16384;

        private sealed class LandblockEntry
        {
            public uint Instance;
            public long Touch;
            public readonly HashSet<uint> Guids = new HashSet<uint>();
        }

        private readonly Dictionary<ushort, LandblockEntry> landblocks = new Dictionary<ushort, LandblockEntry>();
        private readonly Dictionary<uint, (ushort Landblock, byte[] Sequence)> guids = new Dictionary<uint, (ushort, byte[])>();
        private readonly int capacity;
        private long touchClock;

        public ClientObjectLedger(int capacity = DefaultCapacity)
        {
            this.capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>Guids currently filed.</summary>
        public int Count => guids.Count;

        /// <summary>Landblock entries currently held.</summary>
        public int LandblockCount => landblocks.Count;

        /// <summary>Whole landblock entries dropped to stay under the capacity, since this ledger was made.</summary>
        public long Evictions { get; private set; }

        public bool Contains(uint guid) => guids.ContainsKey(guid);

        /// <summary>The instance this client holds <paramref name="landblock"/> from, or null when none is filed.</summary>
        public uint? HeldInstance(ushort landblock)
        {
            return landblocks.TryGetValue(landblock, out var entry) ? entry.Instance : (uint?)null;
        }

        public void Record(uint guid, ushort landblock, uint instance, byte[] instanceSequence)
        {
            if (guids.TryGetValue(guid, out var previous) && previous.Landblock != landblock)
                RemoveFromEntry(guid, previous.Landblock);

            if (landblocks.TryGetValue(landblock, out var entry) && entry.Instance != instance)
            {
                // the client now holds this landblock id from another copy: the old copy's filing is obsolete
                DropEntry(landblock, entry);
                entry = null;
            }

            if (entry == null)
            {
                entry = new LandblockEntry { Instance = instance };
                landblocks.Add(landblock, entry);
            }

            entry.Guids.Add(guid);
            entry.Touch = ++touchClock;
            guids[guid] = (landblock, instanceSequence);

            while (guids.Count > capacity && EvictOldest(landblock))
            {
            }
        }

        public bool Remove(uint guid)
        {
            if (!guids.TryGetValue(guid, out var at))
                return false;

            RemoveFromEntry(guid, at.Landblock);
            return true;
        }

        /// <summary>True when <paramref name="landblock"/> is filed under an instance other than <paramref name="instance"/>.</summary>
        public bool HoldsOtherCopy(ushort landblock, uint instance)
        {
            return landblocks.TryGetValue(landblock, out var entry) && entry.Instance != instance;
        }

        /// <summary>How many guids <see cref="TakeOtherCopies"/> would return, without taking them.</summary>
        public int CountOtherCopies(IEnumerable<ushort> landblockIds, uint instance)
        {
            var count = 0;

            foreach (var landblock in landblockIds)
            {
                if (landblocks.TryGetValue(landblock, out var entry) && entry.Instance != instance)
                    count += entry.Guids.Count;
            }

            return count;
        }

        /// <summary>
        /// Removes and returns every guid filed under any of <paramref name="landblockIds"/> held from an instance
        /// other than <paramref name="instance"/>: the objects the client still holds from another copy of those
        /// landblocks. Ordered by the given landblock order, then by guid.
        /// </summary>
        public List<LedgerObject> TakeOtherCopies(IEnumerable<ushort> landblockIds, uint instance)
        {
            var taken = new List<LedgerObject>();

            foreach (var landblock in landblockIds)
            {
                if (!landblocks.TryGetValue(landblock, out var entry) || entry.Instance == instance)
                    continue;

                var sorted = new List<uint>(entry.Guids);
                sorted.Sort();

                foreach (var guid in sorted)
                    taken.Add(new LedgerObject(guid, guids[guid].Sequence));

                DropEntry(landblock, entry);
            }

            return taken;
        }

        private void RemoveFromEntry(uint guid, ushort landblock)
        {
            guids.Remove(guid);

            if (landblocks.TryGetValue(landblock, out var entry))
            {
                entry.Guids.Remove(guid);

                if (entry.Guids.Count == 0)
                    landblocks.Remove(landblock);
            }
        }

        private void DropEntry(ushort landblock, LandblockEntry entry)
        {
            foreach (var guid in entry.Guids)
                guids.Remove(guid);

            landblocks.Remove(landblock);
        }

        private bool EvictOldest(ushort keep)
        {
            var found = false;
            ushort oldestId = 0;
            LandblockEntry oldest = null;

            foreach (var kvp in landblocks)
            {
                if (kvp.Key == keep)
                    continue;

                if (!found || kvp.Value.Touch < oldest.Touch)
                {
                    found = true;
                    oldestId = kvp.Key;
                    oldest = kvp.Value;
                }
            }

            if (!found)
                return false;

            DropEntry(oldestId, oldest);
            Evictions++;
            return true;
        }
    }
}
